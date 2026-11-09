using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2TargetContinuityRebaseWindowTests
{
    [Fact]
    public void Hd220539ThreeRepeatableFreshFramesReplaceOnlyTheStalePrediction()
    {
        var measured = new[]
        {
            new PixelPoint(787.0056497175141, 388.7231638418079),
            new PixelPoint(787.7051282051282, 390.28205128205127),
            new PixelPoint(787.5693430656934, 388.83941605839414),
        };
        var predictions = new[]
        {
            new PixelPoint(792.485172413793, 396.6182758620688),
            new PixelPoint(792.8251724137931, 397.85827586206904),
            new PixelPoint(792.3451724137931, 396.70827586206894),
        };

        for (var i = 0; i < measured.Length; i++)
            Assert.False(Phd2TargetContinuityPolicy.IsContinuous(measured[i], predictions[i], 6));

        var window = new Phd2TargetContinuityRebaseWindow("HD-220539-same-guide-lock");
        Assert.False(window.Observe(measured[0], out _, out _));
        Assert.False(window.Observe(measured[1], out _, out _));
        Assert.True(window.Observe(measured[2], out var rebased, out var spread));

        Assert.Equal(787.5693430656934, rebased.X, 10);
        Assert.Equal(388.83941605839414, rebased.Y, 10);
        Assert.InRange(spread, 1.7, 1.8);
        Assert.True(window.Consumed);
        Assert.Equal(3, window.SampleCount);
        Assert.False(window.Observe(measured[0], out _, out _));
    }

    [Fact]
    public void FourthFrameStyleOutlierCannotEstablishANewBaseline()
    {
        var window = new Phd2TargetContinuityRebaseWindow("unstable");
        Assert.False(window.Observe(new(787.0, 388.7), out _, out _));
        Assert.False(window.Observe(new(787.7, 390.3), out _, out _));
        Assert.False(window.Observe(new(794.0, 389.0), out _, out var spread));
        Assert.True(spread > Phd2TargetContinuityRebaseWindow.MaximumPairwiseSpreadPixels);
        Assert.False(window.Consumed);
        Assert.Equal(0, window.SampleCount);
    }

    [Fact]
    public void ProductionWindowDrawsOnlyFreshMeasuredPixelsAndPreservesBudgets()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources",
            "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Contains("new Phd2TargetContinuityRebaseWindow(windowKey)", source);
        Assert.Contains("phd2TargetContinuityRebaseConsumedTarget", source);
        Assert.Contains("var sameGuideForRebase = Phd2FreshTargetWindow.SameGuide(", source);
        Assert.Contains("if (!sameGuideForRebase)", source);
        Assert.Contains("!string.Equals(phd2TargetContinuityRebaseConsumedTarget, targetKey", source);
        Assert.Contains("phd2-target-continuity-rebased", source);
        Assert.Contains("rebaseLimitPerTarget = 1", source);
        Assert.Contains("predictionUsedAsMeasurement = false", source);
        Assert.Contains("budgetReset = false", source);
        Assert.Contains("if (!continuityRebaseCandidateObserved)", source);
    }
}
