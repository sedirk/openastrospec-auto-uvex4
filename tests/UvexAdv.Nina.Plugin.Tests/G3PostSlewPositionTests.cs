using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class G3PostSlewPositionTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 4.99)] [InlineData(2, 5)]
    public void ExactExistingEnvelopeStillPasses(double drift, double residual) =>
        Assert.Equal(GateDisposition.Passed, G3WcsRecoveryPolicy.PostSlewPosition(drift, residual, 2, 5, 5).Disposition);

    [Theory]
    [InlineData(0, 5.0001)] [InlineData(1, 5.07)] [InlineData(2, 100)]
    public void StableMissNeverAuthorizesFrameOrClaimsArrival(double drift, double residual)
    {
        var gate = G3WcsRecoveryPolicy.PostSlewPosition(drift, residual, 2, 5, 5);
        Assert.Equal("G3_POST_SLEW_STABLE_ENDPOINT_MISSED", gate.Code);
        Assert.NotEqual(GateDisposition.Passed, gate.Disposition);
        Assert.Equal(GateFailureScope.TargetQuality, gate.FailureScope);
        Assert.Equal(residual, gate.Metrics!["commandResidualArcseconds"]);
    }

    [Theory]
    [InlineData(2.001, 5.07, 2, 5, 5)] [InlineData(3, 0, 2, 5, 5)]
    [InlineData(double.NaN, 6, 2, 5, 5)] [InlineData(0, double.NaN, 2, 5, 5)]
    [InlineData(-1, 6, 2, 5, 5)] [InlineData(0, -1, 2, 5, 5)]
    [InlineData(0, 6, 0, 5, 5)] [InlineData(0, 6, 2, 0, 5)]
    [InlineData(0, 6, 2, 5, 0)] [InlineData(0, 6, 2, double.PositiveInfinity, 5)]
    public async Task UnstableOrInvalidStateStillStopsActualNextTarget(double drift, double residual,
        double driftLimit, double residualLimit, double seconds)
    {
        var source = G3WcsRecoveryPolicy.PostSlewPosition(drift, residual, driftLimit, residualLimit, seconds);
        Assert.Equal("G3_POST_SLEW_POSITION_UNSTABLE", source.Code);
        Assert.Equal(GateFailureScope.Run, source.FailureScope);
        Assert.All(source.Metrics!, m => Assert.True(double.IsFinite(m.Value)));
        var terminal = G3WcsRecoveryPolicy.SearchTerminalGate(
            GateResult.Unknown("G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED", "Return cannot erase instability."),
            source, true, null);
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", terminal.Code,
            terminal, ObservationStage.AcquireG3SlitField);
    }

    [Fact]
    public async Task StableMissWithUnknownReturnStillStopsNextTarget()
    {
        var source = G3WcsRecoveryPolicy.PostSlewPosition(1, 5.07, 2, 5, 5);
        var terminal = G3WcsRecoveryPolicy.SearchTerminalGate(
            GateResult.Unknown("G3_SEARCH_RETURN_BLOCKED", "No origin arrival proven."), source, false,
            GateResult.Unknown("PHD2_OWNER_CHANGED", "Return owner is no longer known."));
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync("unknown-gate", terminal.Code,
            terminal, ObservationStage.AcquireG3SlitField);
    }
}
