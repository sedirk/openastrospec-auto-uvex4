using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3NeighbourSearchPolicyTests
{
    private static G3LocalSearchLimits Limits => new(G3LocalSearchPattern.SquareSpiral, 300, 900, 3600, 12, TimeSpan.FromSeconds(1200));
    private static G3OverlapFootprint Field => new(1920, 1080, .3837484767386058, null, 40.03047514115362);

    [Fact]
    public void BlindSearchKeepsOriginalDestinationsInsteadOfHypotheticalMosaicCoverage()
    {
        var limits = Limits;
        var nominal = G3LocalSearchPlanner.Build(limits);
        var east = 0d; var north = 0d;
        foreach (var point in nominal)
        {
            var visit = G3NeighbourSearchPolicy.PlanVisit(limits, Field, false, point, east, north, point.Attempt);
            Assert.True(visit.ReachesNominalDestination);
            Assert.False(visit.PreservesOverlap);
            Assert.Equal(point, visit.Waypoint);
            east = point.RaTangentOffsetArcseconds; north = point.DeclinationOffsetArcseconds;
        }
        Assert.Equal(300, nominal[0].RadiusArcseconds);
        Assert.InRange(nominal[8].RadiusArcseconds, 670, 671);
        Assert.Equal(limits, Limits); // no mutation of any ceiling
    }

    [Fact]
    public void SupportArrivalAndExpiryDoNotRestartTheOriginalSpiralOrActionNumber()
    {
        var target = G3LocalSearchPlanner.Build(Limits)[0];
        var first = G3NeighbourSearchPolicy.PlanVisit(Limits, Field, true, target, 0, 0, 1);
        Assert.True(first.PreservesOverlap);
        Assert.False(first.ReachesNominalDestination);
        Assert.InRange(first.Waypoint.MoveFromPreviousArcseconds, 116, 118);
        var expired = G3NeighbourSearchPolicy.PlanVisit(Limits, Field, false, target,
            first.Waypoint.RaTangentOffsetArcseconds, first.Waypoint.DeclinationOffsetArcseconds, 2);
        Assert.True(expired.ReachesNominalDestination);
        Assert.Equal(2, expired.Waypoint.Attempt);
        Assert.Equal(300, expired.Waypoint.RaTangentOffsetArcseconds);
        var newSupport = G3NeighbourSearchPolicy.PlanVisit(Limits, Field, true,
            G3LocalSearchPlanner.Build(Limits)[1], 300, 0, 3);
        Assert.True(newSupport.PreservesOverlap);
        Assert.Equal(300, newSupport.Waypoint.RaTangentOffsetArcseconds);
        Assert.Equal(3, newSupport.Waypoint.Attempt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void CannotCreateAdditionalActions(int action) => Assert.Throws<ArgumentException>(() =>
        G3NeighbourSearchPolicy.PlanVisit(Limits, Field, false, G3LocalSearchPlanner.Build(Limits)[0], 0, 0, action));

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(901, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void RejectsInvalidCursor(double east, double north) => Assert.Throws<ArgumentException>(() =>
        G3NeighbourSearchPolicy.PlanVisit(Limits, Field, false, G3LocalSearchPlanner.Build(Limits)[0], east, north, 1));

    [Fact]
    public void AllProposalsStillPassTheOriginalDurableReturnReservationBeforeUse()
    {
        var result = ExerciseOriginalLedger(false);
        Assert.InRange(result.Furthest, 424, 672);
        Assert.InRange(result.Actions, 2, 10);
        Assert.NotEqual(GateDisposition.Passed, result.Stop.Disposition);
        Assert.Contains("RESERVE", result.Stop.Code);
        var overlap = ExerciseOriginalLedger(true);
        Assert.True(overlap.Furthest <= result.Furthest);
        // Both may eventually reach the first corner. Blind search reaches it
        // in two actions rather than six subdivisions; the old 117" grid only
        // reached 261" in the recorded eleven visits.
        Assert.True(G3FieldOverlapPolicy.PlanSearch(Limits, Field).Waypoints.Take(11).Max(p => p.RadiusArcseconds) < result.Furthest);
    }

    public static (double Furthest, int Actions, GateResult Stop) ExerciseOriginalLedger(bool support)
    {
        var now = DateTimeOffset.Parse("2026-10-25T19:13:29Z");
        var state = new G3AcquisitionMotionState(G3AcquisitionMotionState.CurrentSchemaVersion,
            G3AcquisitionMotionState.CurrentTangentProjectionId, "fixture", new string('D',32),
            new string('A',64), new string('B',64), new string('C',64),
            G3AcquisitionMotionKind.LocalSearch, G3AcquisitionMotionPhase.SettledBudgetLedger,
            "pierEast", "JNOW", 45.484811666666666, 38.239463055555554,
            45.484811666666666, 38.239463055555554, 45.484811666666666, 38.239463055555554,
            0, 0, 0, 310, 900, 3600, 12, 2, 90, 1200, 0, 0,
            now, now, now, "fixture-original-declaration.json", "finite no-hardware replay");
        Assert.Empty(state.Validate());
        var nominal = G3LocalSearchPlanner.Build(Limits);
        var index = 0; var furthest = 0d;
        for (var action = 1; action <= 12 && index < nominal.Count; action++)
        {
            var visit = G3NeighbourSearchPolicy.PlanVisit(Limits, Field, support, nominal[index],
                state.CurrentRaTangentOffsetArcseconds, state.CurrentDeclinationOffsetArcseconds, action);
            var p = visit.Waypoint;
            var reserve = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(state,
                p.RaTangentOffsetArcseconds, p.DeclinationOffsetArcseconds, now.AddSeconds(action * 35));
            if (reserve.Gate.Disposition != GateDisposition.Passed) return (furthest, state.CorrectionAttempts, reserve.Gate);
            state = state with { CurrentRaTangentOffsetArcseconds = p.RaTangentOffsetArcseconds,
                CurrentDeclinationOffsetArcseconds = p.DeclinationOffsetArcseconds,
                CumulativeMotionArcseconds = state.CumulativeMotionArcseconds + reserve.MoveFromCurrentArcseconds + 10,
                CorrectionAttempts = state.CorrectionAttempts + 1 };
            Assert.Equal(12, state.MaximumCorrectionAttempts);
            Assert.Equal(1200, state.MaximumElapsedSeconds);
            Assert.Equal(now, state.StartedUtc);
            furthest = Math.Max(furthest, p.RadiusArcseconds);
            if (visit.ReachesNominalDestination) index++;
        }
        return (furthest, state.CorrectionAttempts, GateResult.TargetQuality("G3_SEARCH_ATTEMPTS_EXHAUSTED", "Original action limit, not new allowance."));
    }
}
