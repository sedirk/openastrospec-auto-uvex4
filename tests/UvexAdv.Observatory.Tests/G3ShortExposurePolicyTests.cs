using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3ShortExposurePolicyTests
{
    [Fact]
    public void SaturationLadderReservesNewPairWithoutChangingOrdinaryRetryBudget()
    {
        var policy = new G3ShortExposurePolicy(10);
        Assert.Equal(policy,policy.AfterMeasurement("G3_SEP_UNMEASURED",1));
        Assert.Equal(policy,policy.AfterMeasurement("G3_SEP_AMBIGUOUS",1));
        Assert.Equal(policy,policy.AfterMeasurement("G3_SEP_INVALID_COVERAGE",1));
        var a=policy.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,1);
        var b=a.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,2);
        var c=b.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,3);
        Assert.Equal(new G3ShortExposurePolicy(5,4,1),a);
        Assert.Equal(new G3ShortExposurePolicy(2,5,2),b);
        Assert.Equal(new G3ShortExposurePolicy(1,6,3),c);
        Assert.Equal(c,c.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,4));
    }

    [Fact]
    public void LateReductionKeepsAnAbsoluteLimitAndExposureFloor()
    {
        var policy=new G3ShortExposurePolicy(10).AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,3);
        Assert.Equal(6,policy.MaximumFrames);
        policy=policy.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,4);
        Assert.Equal(2,policy.ExposureMilliseconds);
        Assert.Equal(policy,policy.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,5));
        Assert.Throws<ArgumentOutOfRangeException>(()=>policy.AfterMeasurement("any",7));
        var minimum=new G3ShortExposurePolicy(1);
        Assert.Equal(minimum,minimum.AfterMeasurement(G3ShortExposurePolicy.SaturatedCode,1));
    }

    [Fact]
    public void BoundedConfirmationStillNeedsTwoGoodIndependentFrames()
    {
        var star=new StarCandidate(new(500,400),10000,100000,100,0,.8,0,100);
        var gate=GateResult.Pass("G3_SEP_POSITION_MEASURED","Measured");
        var m=new G3ShortPositionMeasurement(gate,new(gate,star,new(500,400),0,2),true,.5);
        var first=G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null,m,null,new('a',64),4,100,6);
        Assert.False(first.Accepted);
        Assert.True(first.RetryAllowed);
        Assert.True(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(m,m,new('a',64),new('b',64),5,100,6).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(m,m,new('a',64),new('a',64),5,100,6).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(m,m,new('a',64),new('b',64),5,100,7).Accepted);
        Assert.False(G3ShortPositionMeasurementPolicy.EvaluateConfirmation(null,m,null,new('a',64),4,100).RetryAllowed);
    }
}
