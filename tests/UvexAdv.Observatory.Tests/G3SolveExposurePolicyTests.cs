using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3SolveExposurePolicyTests
{
    [Fact]
    public void BrightSkyUsesRemainingSlotForFreshShortFrameNotLongerClipping()
    {
        var shortFrame = Content(4,22112,27168);
        Assert.Equal(5000,G3SolveExposurePolicy.ShortRecheckMilliseconds(5000,10000,shortFrame,65520,5000,shortFrame));
        var longFrame = Content(0,38032,45680);
        Assert.Equal(5000,G3SolveExposurePolicy.ShortRecheckMilliseconds(10000,15000,longFrame,65520,5000,shortFrame));
    }

    [Fact]
    public void BrightBackgroundCapsExposureEvenWhenGlobalStarDetectorMissesSparseField()
    {
        var empty = Content(0, 28896, 34944);
        Assert.Equal(5000, G3SolveExposurePolicy.ShortRecheckMilliseconds(5000, 10000, empty, 65520, null, null));
        Assert.False(empty.HasCoherentSource); // Exposure control never grants search authority.
        var second = Content(0, 36880, 44304);
        Assert.Equal(10000, G3SolveExposurePolicy.ShortRecheckMilliseconds(10000, 15000, second, 65520, null, null));
    }

    [Fact]
    public void DarkSkyEmptyFieldAndExplicitClippingDoNotAcquireNewAuthority()
    {
        Assert.Null(G3SolveExposurePolicy.ShortRecheckMilliseconds(5000,10000,Content(6,500,700),65520,null,null));
        Assert.Null(G3SolveExposurePolicy.ShortRecheckMilliseconds(5000,10000,Content(0,500,700),65520,null,null));
        var clipped = Content(0,65520,65520) with { Gate = GateResult.Unknown(G3SolveProbeContentAnalyzer.OverexposedGateCode,"clipped") };
        Assert.Null(G3SolveExposurePolicy.ShortRecheckMilliseconds(10000,15000,clipped,65520,5000,Content(4,100,500)));
        Assert.Null(G3SolveExposurePolicy.ShortRecheckMilliseconds(5000,5000,Content(4,30000,45000),65520,null,null));
    }

    private static G3SolveProbeContentAssessment Content(int stars,double median,double upper) => new(
        GateResult.Pass("finite-content","not target authority"),
        new(GateResult.Pass("stars","finite fixture"),3,0,stars,stars,0,20,0,1,[]),median,100,20,0)
        { UpperBackgroundAdu = upper };
}
