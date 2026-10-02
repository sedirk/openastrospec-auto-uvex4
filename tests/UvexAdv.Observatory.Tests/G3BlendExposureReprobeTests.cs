using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3BlendExposureReprobeTests
{
    private static G3ShortPositionMeasurement Measure(double x, double y, int children, double peak = 4736)
    {
        var parent = new SepPositionComponent(x, y, 6.97, 4.76, 44718, peak, 77, 0, 178, 487,
            [(int)x - 20, (int)y - 15, 34, 28], 0, 2597);
        var image = new SepImageMeasurements(1, SepStarDetectionClient.Algorithm, 1920, 1080, [], false, false)
        {
            ParentComponents = [parent],
            Components = Enumerable.Range(0, children).Select(i => parent with { X = x + i, SepFlags = children > 1 ? 1 : 0 }).ToArray()
        };
        return G3SepShortPositionPolicy.Measure(image, new(804.1290557991694, 417.92319895753445), 100);
    }

    [Fact]
    public void RecordedV460SplitPositionsSelectFortyMsButNeverAuthorizeTheBlendedCentre()
    {
        // 2026-10-02 11:54Z receipts 73/75. Rounded shape diagnostics; exact
        // observed positions. Raw FITS and JSON stay outside Git.
        var a = Measure(802.727283375067, 420.5730392264958, 3);
        var b = Measure(803.324402229757, 420.2875691572273, 2);
        var decision = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(a, b, new('a', 64), new('b', 64), 3, 100);
        Assert.Equal("G3_SEP_PARENT_BLEND_UNRESOLVED", decision.Gate.Code);
        Assert.InRange(decision.PositionSpreadPixels, .661, .663);
        var next = new G3ShortExposurePolicy(10).AfterConfirmation(decision, b, 3, 65520);
        Assert.Equal(40, next.ExposureMilliseconds);
        Assert.Equal(6, next.MaximumFrames);
        Assert.Equal(1, next.BlendExposureIncreases);
        Assert.False(decision.Accepted);
        var retry = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(a, b, new('a', 64), new('b', 64), 3, 100, next.MaximumFrames);
        Assert.True(retry.RetryAllowed);
        Assert.False(retry.Accepted);
        Assert.Equal("G3_SEP_PARENT_BLEND_UNRESOLVED", retry.Gate.Code);
    }

    [Fact]
    public void ChangedExposureNeedsNewIndependentPairAndKeepsRealBinaryBlocked()
    {
        var split = Measure(803, 420, 2);
        var d = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(split, split, new('a',64), new('b',64), 2, 100);
        var p = new G3ShortExposurePolicy(10).AfterConfirmation(d, split, 2, 65520);
        Assert.Equal(5, p.MaximumFrames);
        var unsplit = Measure(803, 420, 1);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null, unsplit, null, new('c',64), 3, 100, p.MaximumFrames).Accepted);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(unsplit, unsplit, new('c',64), new('d',64), 4, 100, p.MaximumFrames).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(unsplit, unsplit, new('c',64), new('c',64), 4, 100, p.MaximumFrames).Accepted);
        var binary = G3ShortPositionMeasurementPolicy.EvaluateConfirmation(split, split, new('c',64), new('d',64), 5, 100, p.MaximumFrames);
        Assert.False(binary.Accepted);
        Assert.False(binary.RetryAllowed);
        Assert.Equal(p, p.AfterConfirmation(binary, split, 5, 65520));
        var disagree = Measure(808, 420, 1);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(unsplit, disagree, new('c',64), new('d',64), 4, 100, p.MaximumFrames).Accepted);
    }

    [Theory]
    [InlineData("G3_SEP_AMBIGUOUS")]
    [InlineData("G3_SEP_INVALID_COVERAGE")]
    [InlineData("G3_SHORT_FRAME_REUSED")]
    [InlineData("G3_SHORT_POSITION_OUTSIDE")]
    [InlineData("G3_SHORT_REPEAT_DISAGREES")]
    public void OtherFailuresDoNotChangeExposureOrFrameBudget(string code)
    {
        var m = Measure(803,420,2);
        var d = new G3ShortPositionConfirmationDecision(GateResult.Unknown(code,"test"),null,false,false,false,.5);
        var p = new G3ShortExposurePolicy(10);
        Assert.Equal(p,p.AfterConfirmation(d,m,3,65520));
    }

    [Theory]
    [InlineData(14000, 65520)]
    [InlineData(4736, double.NaN)]
    [InlineData(4736, 0)]
    public void InsufficientOrUnknownHeadroomCannotIncrease(double peak,double saturation)
    {
        var m=Measure(803,420,2,peak);
        var d=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(m,m,new('a',64),new('b',64),2,100);
        var p=new G3ShortExposurePolicy(10);
        Assert.Equal(p,p.AfterConfirmation(d,m,2,saturation));
    }

    [Fact]
    public void IncreaseIsOnceCappedAndCannotOscillateAfterSaturation()
    {
        var m=Measure(803,420,2);
        var d=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(m,m,new('a',64),new('b',64),2,100);
        var p=new G3ShortExposurePolicy(100).AfterConfirmation(d,m,2,65520);
        Assert.Equal(200,p.ExposureMilliseconds);
        Assert.Equal(p,p.AfterConfirmation(d,m,3,65520));
        var reduced=p.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,3);
        Assert.Equal(100,reduced.ExposureMilliseconds);
        Assert.Equal(1,reduced.BlendExposureIncreases);
        Assert.Equal(6,reduced.MaximumFrames);
        Assert.Equal(reduced,reduced.AfterConfirmation(d,m,4,65520));
        var alreadyReduced=new G3ShortExposurePolicy(10,5,1);
        Assert.Equal(alreadyReduced,alreadyReduced.AfterConfirmation(d,m,3,65520));
        var late=new G3ShortExposurePolicy(10,6);
        Assert.Equal(late,late.AfterConfirmation(d,m,5,65520));
        var distant=d with { PositionSpreadPixels=4.01 };
        Assert.Equal(new G3ShortExposurePolicy(10),new G3ShortExposurePolicy(10).AfterConfirmation(distant,m,2,65520));
    }
}
