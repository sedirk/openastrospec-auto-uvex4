using UvexAdv.Nina.Plugin;
using UvexAdv.Phd2;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2CoarseHandoffPolicyTests
{
    [Fact]
    public void RecordedWr136RebuildUsesRemainingMotionAndTimeNotInitialEnvelope()
    {
        var radius = Phd2CoarseHandoffPolicy.LimitToRemainingLedger(44, Limits(), 0.5, 2,
            5, 74.96615417841444, TimeSpan.FromSeconds(194.3895));
        Assert.InRange(radius, 7.9, 8.1);
        Assert.True(46.1857 > radius);
        Assert.Equal(2, Phd2CoarseHandoffPolicy.LimitToRemainingLedger(44, Limits(), 0.5, 2,
            5, 74.96615417841444, TimeSpan.FromSeconds(278.8281)));
        Assert.Equal(44, Phd2CoarseHandoffPolicy.LimitToRemainingLedger(44, Limits(), 0.5, 2, 0, 0, TimeSpan.Zero), 6);
    }
    private static Phd2LockShiftLimits Limits() => new(
        25, 100, 8, TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
        1, 1, 2, 100, 20, 20, 2, 4, 3, 1, 1e9);

    [Fact]
    public void HundredPixelRoundTripBudgetCannotAcceptSeventyPixelOneWayHandoff()
    {
        var limits = Limits();
        var maximum = Phd2CoarseHandoffPolicy.MaximumInitialResidual(limits, 0.5, 2);
        Assert.InRange(maximum, 43.99, 44.01);
        Assert.True(71.42 > maximum);
        Assert.Equal(100, limits.MaximumAcquisitionResidualPixels);
        Assert.Equal(100, limits.MaximumCumulativePixels);
        Assert.Equal(20, Phd2CoarseHandoffPolicy.SelectHandoffRadius(false, 20, 100, limits, 0.5, 2));
    }

    [Theory]
    [InlineData(35.4359007393, 2.49)]
    [InlineData(37.1583296727, 3.4679939326)]
    [InlineData(30.5223701788, 1.4248683292)]
    [InlineData(35.2387409441, 1.5234874450)]
    public void RecordedMeasuredNearTargetEntersPhd2BeforeAnotherCoarseSlew(double residual, double spread)
    {
        var limits = Limits();
        var measured = Phd2CoarseHandoffPolicy.SelectHandoffRadius(true, 20, 100, limits, 0.5, 2);
        var catalog = Phd2CoarseHandoffPolicy.SelectHandoffRadius(false, 20, 100, limits, 0.5, 2);
        Assert.InRange(measured, 43.99, 44.01);
        Assert.True(residual + spread <= measured);
        Assert.True(residual + spread > catalog);
        Assert.Equal(25, limits.MaximumStagePixels);
        Assert.Equal(100, limits.MaximumCumulativePixels);
        Assert.Equal(8, limits.MaximumAttempts);
        Assert.Equal(TimeSpan.FromSeconds(300), limits.MaximumElapsed);
    }

    [Fact]
    public void MeasuredHandoffStillReservesReturnAndKeepsRecognitionAndTimeLimits()
    {
        Assert.Equal(10, Phd2CoarseHandoffPolicy.SelectHandoffRadius(true, 20, 10, Limits(), 0.5, 2));
        var shortTime = Phd2CoarseHandoffPolicy.SelectHandoffRadius(true, 20, 100,
            Limits() with { MaximumElapsed = TimeSpan.FromSeconds(60) }, 0.5, 2);
        Assert.InRange(shortTime, 9.49, 9.51);
        var normal = Phd2CoarseHandoffPolicy.SelectHandoffRadius(true, 20, 100, Limits(), 0.5, 2);
        Assert.True(71.42 > normal);
        Assert.True(42 + 3 > normal); // Measurement spread is not free travel.
    }

    [Fact]
    public void ProductionRouteRequiresFormalSolveAndMeasuredIdentityAndKeepsLivePlanner()
    {
        var handoff = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Handoff.cs"));
        Assert.Contains("field.Gate.Disposition == GateDisposition.Passed", handoff);
        Assert.Contains("field.Solve?.Result.Success == true && field.Solve.Result.Coordinates is not null", handoff);
        Assert.Contains("field.TargetIdentification.HasCatalogPositionRefinement", handoff);
        var placement = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sources", "RealObservationStageRunner.Phd2SlitPlacement.cs"));
        Assert.Contains("Phd2HandoffResidualPixels(guideChoice.Field, preset)", placement);
        Assert.Contains("var nextAcquisitionBudget = Phd2SlitLockShiftPlanner.EvaluateAcquisitionBudget(", placement);
        Assert.Contains("if (!nextAcquisitionBudget.IsAllowed)", placement);
        Assert.Contains("HandleDeniedPhd2AcquisitionBudgetAsync(context, session, preset,", placement);
    }

    [Fact]
    public void ActionAndTimeReservationsAlsoLimitTheHandoff()
    {
        var twoActions = Phd2CoarseHandoffPolicy.MaximumInitialResidual(Limits() with { MaximumAttempts = 2 }, 0.5, 2);
        var oneMinute = Phd2CoarseHandoffPolicy.MaximumInitialResidual(Limits() with { MaximumElapsed = TimeSpan.FromSeconds(60) }, 0.5, 2);
        Assert.InRange(twoActions, 9.49, 9.51);
        Assert.InRange(oneMinute, 9.49, 9.51);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.1)]
    public void InvalidScaleCannotCreateCoarseHandoffAuthority(double scale)
    {
        Assert.Throws<ArgumentException>(() => Phd2CoarseHandoffPolicy.MaximumInitialResidual(Limits(), scale, 2));
    }
}
