using System.Text.Json.Nodes;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3VerifiedReturnWaypointTests
{
    [Theory]
    [InlineData(11738.9)] [InlineData(11893.5)]
    public void RecordedRequiredDistanceFitsThreeVerifiedLegsWithoutRefundingAnyBudget(double required)
    {
        // Only required travel/limits are historical. Destination measurements
        // are explicit finite simulation, NOT replayed real-sky successes.
        var initial = State();
        var state = initial;
        var travelled = 0d;
        while (travelled < required - 0.001)
        {
            var previous = travelled;
            travelled = Math.Min(required, travelled + 5394);
            state = Arrive(state, travelled);
            var beforePromotion = state;
            state = Assert.IsType<G3AcquisitionMotionState>(G3VerifiedReturnWaypointPolicy.Promote(state,
                Proof(state), true, true, required - previous, required - travelled));
            Assert.Equal(beforePromotion.CumulativeMotionArcseconds, state.CumulativeMotionArcseconds);
            Assert.Equal(beforePromotion.CorrectionAttempts, state.CorrectionAttempts);
            Assert.Equal(initial.StartedUtc, state.StartedUtc);
            Assert.Equal(initial.OriginRaDegrees, state.OriginRaDegrees);
            Assert.Equal(initial.OriginDeclinationDegrees, state.OriginDeclinationDegrees);
            Assert.Equal(initial.MaximumRadiusArcseconds, state.MaximumRadiusArcseconds);
            Assert.Equal(initial.MaximumCumulativeMotionArcseconds, state.MaximumCumulativeMotionArcseconds);
            Assert.Equal(initial.MaximumCorrectionAttempts, state.MaximumCorrectionAttempts);
            Assert.Equal(initial.MaximumElapsedSeconds, state.MaximumElapsedSeconds);
            Assert.InRange(state.CurrentReturnDistanceArcseconds, 0, 0.01);
        }
        Assert.Equal(3, state.CorrectionAttempts);
        Assert.InRange(state.CumulativeMotionArcseconds, required, required + 6.01);
        Assert.InRange(state.CurrentRadiusArcseconds, required - .01, required + .01);
        Assert.Empty(state.Validate());
    }

    [Fact]
    public void FailedDestinationDoesNotAdvanceAndReturnRemainsReservedToPreviousWaypoint()
    {
        var first = Arrive(State(), 5394);
        first = Assert.IsType<G3AcquisitionMotionState>(G3VerifiedReturnWaypointPolicy.Promote(first, Proof(first), true, true, 11893, 6499));
        var failed = Arrive(first, 10788);
        Assert.Null(G3VerifiedReturnWaypointPolicy.Promote(failed, Proof(failed), false, true, 6499, 1105));
        Assert.Equal(first.VerifiedReturnWaypoint, failed.VerifiedReturnWaypoint);
        var step = G3AcquisitionMotionPlanner.PlanNextReturnStep(failed, failed.PriorReportedRaDegrees,
            failed.PriorReportedDeclinationDegrees, 10, failed.UpdatedUtc.AddSeconds(10));
        Assert.Equal(GateDisposition.Passed, step.Gate.Disposition);
        Assert.InRange(step.CurrentRadiusArcseconds, 5393, 5395);
        Assert.True(step.CommandedDeclinationDegrees < failed.PriorReportedDeclinationDegrees);
        Assert.True(step.CommandedDeclinationDegrees >= first.ReturnDeclinationDegrees - 1e-8);
        Assert.True(failed.CumulativeMotionArcseconds + 2 * failed.MaximumSingleCorrectionArcseconds <= 21600);
        Assert.Equal(GateDisposition.Passed, G3AcquisitionReturnTimePolicy.Plan(failed, failed.UpdatedUtc.AddSeconds(10)).Gate.Disposition);
    }

    [Theory]
    [InlineData("inferred")] [InlineData("owner-or-pier")] [InlineData("stale")]
    [InlineData("non-progress")] [InlineData("search")] [InlineData("pending-command")]
    [InlineData("wrong-position")] [InlineData("wrong-action")] [InlineData("hash")]
    public void UnverifiedDestinationNeverAdvancesWaypoint(string reason)
    {
        var state = Arrive(State(), 1000);
        var proof = Proof(state);
        state = reason switch {
            "search" => state with { Kind = G3AcquisitionMotionKind.LocalSearch },
            "pending-command" => state with { Phase = G3AcquisitionMotionPhase.OutboundIntent },
            _ => state };
        proof = reason switch {
            "stale" => proof with { FrameCompletedUtc = state.UpdatedUtc },
            "wrong-position" => proof with { DeclinationDegrees = proof.DeclinationDegrees + .01 },
            "wrong-action" => proof with { ChargedAction = 0 },
            "hash" => proof with { FormalEvidenceSha256 = "bad" },
            _ => proof };
        Assert.Null(G3VerifiedReturnWaypointPolicy.Promote(state, proof, reason != "inferred",
            reason != "owner-or-pier", 11893, reason == "non-progress" ? 11893 : 10893));
        Assert.Null(state.VerifiedReturnWaypoint);
    }

    [Fact]
    public void WaypointDoesNotExpandOriginalRadiusOrResetExhaustedCountsAndTime()
    {
        var arrived = Arrive(State(), 5394);
        var state = Assert.IsType<G3AcquisitionMotionState>(G3VerifiedReturnWaypointPolicy.Promote(arrived, Proof(arrived), true, true, 11893, 6499));
        var at = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(state.OriginRaDegrees,
            state.OriginDeclinationDegrees, state.OriginRaDegrees, state.OriginDeclinationDegrees + 6000d / 3600);
        var now = state.UpdatedUtc;
        foreach (var exhausted in new[] { state with { CumulativeMotionArcseconds = 21600 },
            state with { CorrectionAttempts = state.MaximumCorrectionAttempts },
            state with { StartedUtc = state.StartedUtc.AddHours(-1) } })
            Assert.NotEqual(GateDisposition.Passed, G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
                exhausted, at.RaArcseconds, at.DecArcseconds, now).Gate.Disposition);
        var smallerRadius = state with { MaximumRadiusArcseconds = 5500 };
        Assert.Equal("G3_MOTION_RADIUS_LIMIT", G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
            smallerRadius, at.RaArcseconds, at.DecArcseconds, now).Gate.Code);
    }

    [Fact]
    public async Task OldPayloadOmissionAndNewWaypointCanonicalHashAreBothPreserved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"g3-waypoint-{Guid.NewGuid():N}.json");
        try
        {
            await G3AcquisitionMotionStore.WriteAtomicAsync(path, State());
            Assert.DoesNotContain("verifiedReturnWaypoint", await File.ReadAllTextAsync(path));
            Assert.NotNull((await G3AcquisitionMotionStore.LoadAsync(path)).State);
            var arrived = Arrive(State(), 1000);
            var promoted = Assert.IsType<G3AcquisitionMotionState>(G3VerifiedReturnWaypointPolicy.Promote(arrived, Proof(arrived), true, true, 11893, 10893));
            await G3AcquisitionMotionStore.WriteAtomicAsync(path, promoted);
            Assert.Equal(promoted, (await G3AcquisitionMotionStore.LoadAsync(path)).State);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["state"]!["verifiedReturnWaypoint"]!["raDegrees"] = 11;
            await File.WriteAllTextAsync(path, json.ToJsonString());
            Assert.Null((await G3AcquisitionMotionStore.LoadAsync(path)).State);
        }
        finally { File.Delete(path); }
    }

    private static G3AcquisitionMotionState Arrive(G3AcquisitionMotionState state, double total)
    {
        var dec = state.OriginDeclinationDegrees + total / 3600;
        var next = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(state.OriginRaDegrees,
            state.OriginDeclinationDegrees, state.OriginRaDegrees, dec);
        var now = state.UpdatedUtc.AddSeconds(10);
        var reserve = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(state, next.RaArcseconds, next.DecArcseconds, now);
        Assert.Equal(GateDisposition.Passed, reserve.Gate.Disposition);
        return state with { Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
            PriorReportedRaDegrees = state.OriginRaDegrees, PriorReportedDeclinationDegrees = dec,
            CommandedRaDegrees = state.OriginRaDegrees, CommandedDeclinationDegrees = dec,
            CurrentRaTangentOffsetArcseconds = next.RaArcseconds, CurrentDeclinationOffsetArcseconds = next.DecArcseconds,
            CommandMagnitudeArcseconds = reserve.MoveFromCurrentArcseconds,
            CumulativeMotionArcseconds = state.CumulativeMotionArcseconds + reserve.MoveFromCurrentArcseconds + state.ArrivalToleranceArcseconds,
            CorrectionAttempts = state.CorrectionAttempts + 1, UpdatedUtc = now };
    }
    private static G3VerifiedReturnWaypoint Proof(G3AcquisitionMotionState state) => new(state.PriorReportedRaDegrees,
        state.PriorReportedDeclinationDegrees, state.CorrectionAttempts, state.UpdatedUtc.AddSeconds(5),
        state.UpdatedUtc.AddSeconds(6), "distinct-frame.fit", new('D', 64), "formal.json", new('E', 64), new('F', 64));
    private static G3AcquisitionMotionState State()
    {
        var t = DateTimeOffset.Parse("2026-10-30T12:48:29Z");
        return new(2, G3AcquisitionMotionState.CurrentTangentProjectionId, "run", new('1', 32),
            new('A', 64), new('B', 64), new('C', 64), G3AcquisitionMotionKind.WcsCentering,
            G3AcquisitionMotionPhase.SettledBudgetLedger, "pierEast", "JNOW", 353.8237758333, 20.9929288889,
            353.8237758333, 20.9929288889, 353.8237758333, 20.9929288889, 0, 0, 0,
            5400, 18000, 21600, 8, 2, 90, 1200, 0, 0, t, t, t, "declared.json");
    }
}
