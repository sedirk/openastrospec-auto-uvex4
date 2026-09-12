using UvexAdv.Observatory;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2HandoffRecoveryPolicyTests
{
    [Theory]
    [InlineData(14.345, false)]
    [InlineData(20, false)]
    [InlineData(69.28, true)]
    [InlineData(515.328, true)]
    public void EverySolvedSearchExitMustUseTheSameMeasuredCoarseWindow(double residual, bool center) =>
        Assert.Equal(center, G3WcsRecoveryPolicy.NeedsCoarseCentering(GateResult.Pass("G3_FIELD_ANALYZED", "frame"), true, residual, 20));

    [Fact]
    public void UnsolvedImageCannotAuthorizeACoarseMove() => Assert.False(
        G3WcsRecoveryPolicy.NeedsCoarseCentering(GateResult.Pass("BRIGHT_TARGET", "frame"), false, 69, 20));

    [Theory]
    [InlineData("SLIT_LOCK_ACQUISITION_CUMULATIVE_RESERVE", 69, true)]
    [InlineData("SLIT_RESIDUAL_SEARCH_WINDOW", 509, true)]
    [InlineData("SLIT_LOCK_RETURN_TIME_RESERVE", 3, false)]
    [InlineData("G3_FRAME_REUSED", 69, false)]
    [InlineData("GUIDE_LOCK_RESIDUAL_HIGH", 69, false)]
    [InlineData("PHD2_LOCK_LEDGER_BINDING_CHANGED", 69, false)]
    public void RecoveryIsExplicitAndDoesNotDestroyNearSlitWarningPath(string code, double residual, bool expected) =>
        Assert.Equal(expected, Phd2HandoffRecoveryPolicy.ShouldReacquire(
            new(false, code, "test", 0, 0, 0, 0, 0, 0, 0), residual, 7));
}
