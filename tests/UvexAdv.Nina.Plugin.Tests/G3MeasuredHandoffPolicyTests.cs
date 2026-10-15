using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3MeasuredHandoffPolicyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-14T19:00:00Z");
    internal static G3AcquisitionMotionState State() => new(2, G3AcquisitionMotionState.CurrentTangentProjectionId,
        "replay", "11111111111111111111111111111111", new('A',64),new('B',64),new('C',64),
        G3AcquisitionMotionKind.WcsCentering, G3AcquisitionMotionPhase.SettledBudgetLedger, "pierEast", "JNOW",
        53, 47, 53,47,53,47,0,0,0,5400,18000,21600,12,2,90,1200,1500,3,Start,Start,Start,"declaration.json");

    [Fact]
    public void Hd21641NinePointZeroOneArcsecondsIsDebitedWithFullReturnReserveNotAReset()
    {
        var state = State();
        var result = G3MeasuredHandoffPolicy.Charge(state, 9.01, 706, Start.AddSeconds(384));
        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        var after = Assert.IsType<G3AcquisitionMotionState>(result.Charged);
        Assert.Equal(state.CumulativeMotionArcseconds+9.01, after.CumulativeMotionArcseconds);
        Assert.Equal(state.CorrectionAttempts+1, after.CorrectionAttempts);
        Assert.Equal(state with { CumulativeMotionArcseconds=after.CumulativeMotionArcseconds,
            CorrectionAttempts=after.CorrectionAttempts,UpdatedUtc=after.UpdatedUtc }, after);
        Assert.Equal(1, result.Gate.Metrics!["reservedReturnActions"]);
    }

    [Theory]
    [InlineData("distance")] [InlineData("attempts")] [InlineData("time")]
    [InlineData("single")] [InlineData("radius")]
    public async Task ProducedBudgetFailureExecutesNextNativeTargetOnlyAfterCheckedStop(string failure)
    {
        var state = State();
        var now=Start.AddSeconds(384);
        var distance=9.01;
        var radius=706d;
        if(failure=="distance") state=state with { CumulativeMotionArcseconds=20000 };
        if(failure=="attempts") state=state with { CorrectionAttempts=11 };
        if(failure=="time") now=Start.AddSeconds(1190);
        if(failure=="single") distance=5401;
        if(failure=="radius") radius=18003;
        var result=G3MeasuredHandoffPolicy.Charge(state,distance,radius,now);
        Assert.Null(result.Charged);
        Assert.Equal("G3_FRESH_HANDOFF_BUDGET_EXHAUSTED",result.Gate.Code);
        Assert.Equal(GateFailureScope.TargetQuality,result.Gate.FailureScope);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("typed-skip",result.Gate.Code,result.Gate,ObservationStage.AcquireG3SlitField);
    }

    [Theory]
    [InlineData("cleanup-failure")] [InlineData("end-policy")]
    [InlineData("late-unsafe")] [InlineData("late-cancel")]
    [InlineData("late-dawn")] [InlineData("late-deadline")]
    public async Task HandoffBudgetDoesNotOverrideIndependentNightBoundary(string mode)
    {
        var result=G3MeasuredHandoffPolicy.Charge(State() with {CorrectionAttempts=11},9.01,706,Start.AddSeconds(384));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("typed-"+mode,result.Gate.Code,result.Gate,ObservationStage.AcquireG3SlitField);
    }

    [Fact]
    public void InvalidLedgerOrClockIsNotAQualitySkip()
    {
        foreach(var result in new[] {
            G3MeasuredHandoffPolicy.Charge(State(),double.NaN,706,Start),
            G3MeasuredHandoffPolicy.Charge(State(),9.01,706,Start.AddSeconds(-1)),
            G3MeasuredHandoffPolicy.Charge(State() with {Phase=G3AcquisitionMotionPhase.OutboundIntent},9.01,706,Start) })
        {
            Assert.Equal(GateFailureScope.Run,result.Gate.FailureScope);
            Assert.Null(result.Charged);
        }
    }
}
