using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitFieldAnalysisTests
{
    [Fact]
    public void DetectorFindsBroadDefocusedStar()
    {
        const int width = 160, height = 120;
        var pixels = Enumerable.Repeat((ushort)500, width * height).ToArray();
        AddGaussian(pixels, width, height, 73.4, 52.7, 3.2, 18000);
        var frame = new MonochromeFrame(width, height, pixels, 65520);

        var stars = StarFieldDetector.Detect(frame, new StarDetectionOptions(4, 6));

        var star = Assert.Single(stars);
        Assert.InRange(star.Centroid.X, 72.8, 74.0);
        Assert.InRange(star.Centroid.Y, 52.1, 53.3);
        Assert.InRange(star.FwhmPixels, 5, 10);
    }

    [Fact]
    public void TargetIdentityUsesWcsPredictionRatherThanBrightestStar()
    {
        var expected = new StarCandidate(new PixelPoint(100, 100), 5000, 20000, 30, 5, 0.1, 0, 100);
        var brighterWrong = new StarCandidate(new PixelPoint(150, 150), 30000, 100000, 100, 5, 0.1, 0, 100);

        var result = SlitTargetIdentifier.Identify([brighterWrong, expected], new PixelPoint(102, 99), 20);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Same(expected, result.Target);
    }

    [Fact]
    public void GuideStarInsideSlitGuardIsRejected()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 90, 150, 3, 1, "g3", 1, 1);
        var onSlit = new StarCandidate(new PixelPoint(102, 130), 10000, 50000, 50, 4, 0.1, 0, 100);
        var offSlit = new StarCandidate(new PixelPoint(140, 130), 8000, 40000, 40, 4, 0.1, 0, 100);

        var result = GuideStarSelector.Select([onSlit, offSlit], slit, new PixelPoint(100, 100));

        Assert.Equal(offSlit, result.Star);
    }

    [Fact]
    public void ClosestSlitPointPreservesPositionAlongTheUsableAperture()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 0, 150, 3.5, 1, "g3", 1, 1);
        var target = new PixelPoint(45, 106);

        var closest = GuideStarSelector.ClosestPointOnSlit(target, slit);

        Assert.Equal(45, closest.X, 9);
        Assert.Equal(100, closest.Y, 9);
        Assert.Equal(6, GuideStarSelector.DistanceToSlit(target, slit), 9);
    }

    [Fact]
    public void ClosestSlitPointClampsToFiniteEndpointOutsideTheAperture()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 0, 150, 3.5, 1, "g3", 1, 1);
        var target = new PixelPoint(200, 110);

        var closest = GuideStarSelector.ClosestPointOnSlit(target, slit);

        Assert.Equal(175, closest.X, 9);
        Assert.Equal(100, closest.Y, 9);
        Assert.Equal(Math.Sqrt(725), GuideStarSelector.DistanceToSlit(target, slit), 9);
    }

    [Fact]
    public void BroadBrightHaloPeakIsRejectedInFavorOfCompactGuideStar()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 90, 150, 3, 1, "g3", 1, 1);
        var target = new StarCandidate(new PixelPoint(100, 100), 65535, 500000, 300, 0, 0, 1, 100);
        var haloPeak = new StarCandidate(new PixelPoint(260, 100), 40000, 200000, 200, 9, 0.1, 0, 100);
        var compactGuide = new StarCandidate(new PixelPoint(300, 140), 9000, 45000, 30, 4.1, 0.17, 0, 100);

        var result = GuideStarSelector.Select([haloPeak, compactGuide], slit, target);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(compactGuide, result.Star);
    }

    [Fact]
    public void UltraBrightTargetUsesWideHaloGuardBeforeGuideSelection()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 90, 150, 3, 1, "g3", 1, 1);
        var target = new StarCandidate(new PixelPoint(100, 100), 65535, 500000, 300, 0, 0, 1, 100);
        var compactHaloIsland = new StarCandidate(new PixelPoint(180, 100), 12000, 50000, 40, 3.5, 0.1, 0, 100);
        var isolatedGuide = new StarCandidate(new PixelPoint(260, 130), 9000, 40000, 25, 4, 0.1, 0, 100);

        var result = GuideStarSelector.Select([compactHaloIsland, isolatedGuide], slit, target);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(isolatedGuide, result.Star);
        Assert.Contains("120px", result.Gate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePhd2SelectionIsValidatedWithoutSubstitutingAnotherCandidate()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 0, 150, 3.5, 1, "g3", 1, 1);
        var target = new StarCandidate(new PixelPoint(100, 100), 65535, 500000, 300, 0, 0, 1, 100);
        var native = new StarCandidate(new PixelPoint(300, 140), 9000, 45000, 30, 4.1, 0.17, 0, 100);
        var higherScore = new StarCandidate(new PixelPoint(360, 150), 12000, 80000, 60, 3.5, 0.08, 0, 100);

        var result = GuideStarSelector.ValidateNativeSelection(
            [higherScore, native],
            slit,
            target,
            new PixelPoint(300.5, 139.5),
            5);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(native, result.Star);
        Assert.Contains("did not rank or substitute", result.Gate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedNativePhd2HaloIsNotReplacedByACompactCandidate()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 0, 150, 3.5, 1, "g3", 1, 1);
        var target = new StarCandidate(new PixelPoint(100, 100), 65535, 500000, 300, 0, 0, 1, 100);
        var halo = new StarCandidate(new PixelPoint(180, 100), 12000, 50000, 40, 3.5, 0.1, 0, 100);
        var compact = new StarCandidate(new PixelPoint(300, 140), 9000, 45000, 30, 4.1, 0.17, 0, 100);

        var result = GuideStarSelector.ValidateNativeSelection(
            [halo, compact],
            slit,
            target,
            new PixelPoint(180, 100),
            5);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal(halo, result.Star);
        Assert.Contains("without substitution", result.Gate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DarkSlitLocusIsRefinedFromSeedWithoutOperatorConfirmation()
    {
        const int width = 240, height = 180;
        var pixels = Enumerable.Repeat((ushort)1000, width * height).ToArray();
        for (var y = 20; y < 160; y++)
        for (var x = 126; x <= 128; x++)
            pixels[y * width + x] = 850;
        var frame = new MonochromeFrame(width, height, pixels, 65520);
        var seed = new SlitGeometry("seed", new PixelPoint(120, 90), 90, 140, 3, 8, "g3", 1, 1);

        var result = SlitLocusDetector.DetectDarkSlit(frame, seed, 15, 0, 2);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.Geometry.AcquisitionPoint.X, 125.5, 128.5);
    }

    [Fact]
    public void CorrectionRefusesCumulativeLimit()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 90, 150, 3, 1, "g3", 1, 1);
        var transform = new PixelToMountTransform("xform", 2, 0, 0, 2, "East", 0.5, DateTimeOffset.UtcNow);
        var limits = new MotionLimits(0.5, 0.1, 5);

        var result = SlitCorrectionCalculator.Calculate(new PixelPoint(0, 100), slit, transform, limits, 0.08);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("MOTION_CUMULATIVE_LIMIT", result.Gate.Code);
    }

    [Fact]
    public void CorrectionRemovesOnlyCrossSlitErrorForTargetInsideSlitLength()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(100, 100), 0, 150, 3.5, 1, "g3", 1, 1);
        var transform = new PixelToMountTransform("xform", 1, 0, 0, 1, "West", 0.5, DateTimeOffset.UtcNow);
        var limits = new MotionLimits(30d / 3600, 120d / 3600, 4);

        var result = SlitCorrectionCalculator.Calculate(
            new PixelPoint(45, 106),
            slit,
            transform,
            limits,
            cumulativeCorrectionDegrees: 0);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal(0, result.DeltaRaArcseconds, 9);
        Assert.Equal(-6, result.DeltaDecArcseconds, 9);
        Assert.Equal(6d / 3600, result.RequestedMagnitudeDegrees, 9);
    }

    [Fact]
    public void CorrectionCapsLargeRequestToOneClosedLoopSegment()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(102, 0), 90, 150, 3, 1, "g3", 1, 1);
        var transform = new PixelToMountTransform("xform", 1, 0, 0, 1, "East", 0.5, DateTimeOffset.UtcNow);
        var limits = new MotionLimits(30d / 3600, 120d / 3600, 4);

        var result = SlitCorrectionCalculator.Calculate(
            new PixelPoint(0, 0),
            slit,
            transform,
            limits,
            cumulativeCorrectionDegrees: 0,
            completedCorrectionAttempts: 0);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.Equal("MOTION_SEGMENT_BOUNDED", result.Gate.Code);
        Assert.True(result.IsSegmented);
        Assert.Equal(102d / 3600, result.RequestedMagnitudeDegrees, 10);
        Assert.Equal(30d / 3600, result.MagnitudeDegrees, 10);
        Assert.Equal(30, result.DeltaRaArcseconds, 10);
        Assert.Equal(0, result.DeltaDecArcseconds, 10);
        Assert.Equal(4, result.ReservedSegmentCount);
    }

    [Fact]
    public void CorrectionReservesAllRequiredAttemptsBeforeFirstSegment()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(102, 0), 90, 150, 3, 1, "g3", 1, 1);
        var transform = new PixelToMountTransform("xform", 1, 0, 0, 1, "East", 0.5, DateTimeOffset.UtcNow);
        var limits = new MotionLimits(30d / 3600, 120d / 3600, 4);

        var result = SlitCorrectionCalculator.Calculate(
            new PixelPoint(0, 0),
            slit,
            transform,
            limits,
            cumulativeCorrectionDegrees: 0,
            completedCorrectionAttempts: 1);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("CORRECTION_ATTEMPT_RESERVE", result.Gate.Code);
    }

    [Fact]
    public void CorrectionReservesFullMeasuredCumulativeBudgetBeforeFirstSegment()
    {
        var slit = new SlitGeometry("slit", new PixelPoint(102, 0), 90, 150, 3, 1, "g3", 1, 1);
        var transform = new PixelToMountTransform("xform", 1, 0, 0, 1, "East", 0.5, DateTimeOffset.UtcNow);
        var limits = new MotionLimits(30d / 3600, 120d / 3600, 5);

        var result = SlitCorrectionCalculator.Calculate(
            new PixelPoint(0, 0),
            slit,
            transform,
            limits,
            cumulativeCorrectionDegrees: 20d / 3600,
            completedCorrectionAttempts: 0);

        Assert.Equal(GateDisposition.Failed, result.Gate.Disposition);
        Assert.Equal("MOTION_CUMULATIVE_LIMIT", result.Gate.Code);
    }

    private static void AddGaussian(ushort[] pixels, int width, int height, double cx, double cy, double sigma, double amplitude)
    {
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = 500 + amplitude * Math.Exp(-((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (2 * sigma * sigma));
            pixels[y * width + x] = (ushort)Math.Min(65520, Math.Round(value));
        }
    }
}
