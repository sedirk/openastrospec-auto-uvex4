using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class ObservationAutomaticRecoveryPolicyTests
{
    [Theory]
    [InlineData(ObservationStage.SlewToCatalogTarget, "TELESCOPE_TRACKING_ENABLE_TIMEOUT", ObservationAutomaticRecoveryAction.RetrySameStage, 2)]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_FIELD_MOUNT_BINDING_STALE", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_FIELD_MOUNT_BINDING_FRAME_MISSING", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_FIELD_MOUNT_BINDING_FRAME_UNREADABLE", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_FIELD_MOUNT_BINDING_READBACK_UNAVAILABLE", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_TRUSTED_PL3_CARRY_FORWARD_STALE", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "BRIGHT_TARGET_ONLY_ANNULAR_GHOSTS", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "BRIGHT_TARGET_TOPOLOGY_UNPROVEN", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_TARGET_REQUIRED", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_STALE", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_FRAME_MISSING", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_FRAME_UNREADABLE", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_READBACK_UNAVAILABLE", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_FAILURE_RETURNED", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.StartGuiding, "G3_FIELD_REQUIRED", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.RunScienceBlock, "GUIDING_LOST", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.FinalizeObservation, "FINALIZE_INCOMPLETE", ObservationAutomaticRecoveryAction.RetryTerminalCleanup, 2)]
    [InlineData(ObservationStage.ValidateNightSetup, "UVEX_AUTO_CONNECT_FAILED", ObservationAutomaticRecoveryAction.RetrySameStage, 2)]
    [InlineData(ObservationStage.CoarseCenter, "QHY_NATIVE_CENTER_SOURCE_FRAME_MISSING", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_CLOUD_OR_TRANSPARENCY_INVALID", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 3)]
    [InlineData(ObservationStage.StartGuiding, "PHD2_PLACEMENT_SETTLE_STALE", ObservationAutomaticRecoveryAction.RebuildStageDependencies, 1)]
    [InlineData(ObservationStage.RunScienceBlock, "ATR_TIER_NOT_SELECTED", ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, 1)]
    public void ExactReviewedPairsReceiveBoundedRecovery(
        ObservationStage stage,
        string code,
        ObservationAutomaticRecoveryAction action,
        int attempts)
    {
        var plan = ObservationAutomaticRecoveryPolicy.For(
            stage,
            GateResult.Unknown(code, "fault injection"));

        Assert.True(plan.IsRecoverable);
        Assert.Equal(action, plan.Action);
        Assert.Equal(attempts, plan.MaximumAttempts);
        Assert.True(plan.Delay >= TimeSpan.Zero);
    }

    [Fact]
    public void PlacementBindingStalenessRebuildsG3OnceThenExhausts()
    {
        var session = new ObservationAutomaticRecoverySession();
        var gate = GateResult.Unknown(
            "G3_FIELD_MOUNT_BINDING_STALE",
            "fault-injected stale placement source");

        var first = session.Evaluate(ObservationStage.PlaceTargetOnSlit, gate);
        var exhausted = session.Evaluate(ObservationStage.PlaceTargetOnSlit, gate);

        Assert.True(first.ShouldRetry);
        Assert.Equal(ObservationAutomaticRecoveryAction.RebuildStageDependencies, first.Plan.Action);
        Assert.Equal(1, first.AttemptNumber);
        Assert.False(exhausted.ShouldRetry);
        Assert.True(exhausted.Exhausted);
        Assert.Equal(1, exhausted.TotalAttempts);
    }

    [Fact]
    public void FreshlyReturnedPHD2LockRebuildsDependenciesOnceWithoutNewBudget()
    {
        var session = new ObservationAutomaticRecoverySession();
        var gate = GateResult.Unknown(
            "PHD2_LOCK_FAILURE_RETURNED",
            "fresh residual verified the durable origin and checked-stop succeeded");

        var rebuild = session.Evaluate(ObservationStage.PlaceTargetOnSlit, gate);
        var exhausted = session.Evaluate(ObservationStage.PlaceTargetOnSlit, gate);

        Assert.True(rebuild.ShouldRetry);
        Assert.Equal(ObservationAutomaticRecoveryAction.RebuildStageDependencies, rebuild.Plan.Action);
        Assert.Equal(1, rebuild.Plan.MaximumAttempts);
        Assert.Contains("without issuing a new budget", rebuild.Plan.Reason, StringComparison.Ordinal);
        Assert.False(exhausted.ShouldRetry);
        Assert.True(exhausted.Exhausted);
        Assert.Equal(1, exhausted.TotalAttempts);
    }

    [Fact]
    public void TransientLadderExhaustionGetsOneFreshLadderThenHardStops()
    {
        var session = new ObservationAutomaticRecoverySession();
        var gate = GateResult.Unknown(
            "G3_PLATE_SOLVE_LADDER_TRANSIENT_EXHAUSTED",
            "fault-injected single-tier capture/solver outage");

        var freshLadder = session.Evaluate(ObservationStage.AcquireG3SlitField, gate);
        var exhausted = session.Evaluate(ObservationStage.AcquireG3SlitField, gate);

        Assert.True(freshLadder.ShouldRetry);
        Assert.Equal(ObservationAutomaticRecoveryAction.RetryWithFreshStageEvidence, freshLadder.Plan.Action);
        Assert.Equal(1, freshLadder.AttemptNumber);
        Assert.False(exhausted.ShouldRetry);
        Assert.True(exhausted.Exhausted);
        Assert.Equal(1, exhausted.TotalAttempts);
    }

    [Theory]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_BOUNDED_SEARCH_EXHAUSTED_RETURNED")]
    [InlineData(ObservationStage.AcquireG3SlitField, "QHY_MOUNT_COORDINATE_SYNC_READBACK_FAILED")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_LEDGER_DISCOVERY_UNTRUSTED")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "PHD2_LOCK_RETURN_ATTEMPT_LIMIT")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "PHD2_SLIT_PLACEMENT_FAILED_SAFE")]
    [InlineData(ObservationStage.ValidateNightSetup, "HORIZON_BLOCKED")]
    [InlineData(ObservationStage.ValidateNightSetup, "REAL_PROFILE_DRIFT")]
    [InlineData(ObservationStage.AcquireG3SlitField, "G3_TOPOLOGY_FINGERPRINT_CHANGED")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_HASH_INVALID")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_CONTEXT_CHANGED")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_EPOCH_CHANGED")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_FIELD_MOUNT_BINDING_PIER_CHANGED")]
    [InlineData(ObservationStage.PlaceTargetOnSlit, "G3_TRUSTED_PL3_TOPOLOGY_CHANGED")]
    [InlineData(ObservationStage.AcquireG3SlitField, "SOME_NEW_UNREVIEWED_ERROR")]
    public void SafetyIdentityLedgerBudgetAndUnknownErrorsRemainHardStops(
        ObservationStage stage,
        string code)
    {
        var plan = ObservationAutomaticRecoveryPolicy.For(
            stage,
            GateResult.Fail(code, "fault injection"));

        Assert.False(plan.IsRecoverable);
        Assert.Equal(ObservationAutomaticRecoveryAction.None, plan.Action);
        Assert.Equal(0, plan.MaximumAttempts);
    }

    [Fact]
    public void SameCodeInUnreviewedStageDoesNotInheritRecovery()
    {
        var plan = ObservationAutomaticRecoveryPolicy.For(
            ObservationStage.StartGuiding,
            GateResult.Unknown("G3_FIELD_MOUNT_BINDING_STALE", "wrong stage"));

        Assert.False(plan.IsRecoverable);
    }

    [Fact]
    public void PassingWarningsAreNeverRetried()
    {
        var plan = ObservationAutomaticRecoveryPolicy.For(
            ObservationStage.RunScienceBlock,
            GateResult.Warn("GUIDING_LOST", "warning and continue"));

        Assert.False(plan.IsRecoverable);
    }

    [Theory]
    [InlineData("G3_FIELD_MOUNT_BINDING_HASH_INVALID")]
    [InlineData("G3_FIELD_MOUNT_BINDING_FRAME_CHANGED")]
    [InlineData("G3_FIELD_MOUNT_BINDING_CONTEXT_CHANGED")]
    [InlineData("G3_FIELD_MOUNT_BINDING_EPOCH_CHANGED")]
    [InlineData("G3_FIELD_MOUNT_BINDING_PIER_CHANGED")]
    [InlineData("G3_TRUSTED_PL3_EVIDENCE_CHANGED")]
    [InlineData("G3_TRUSTED_PL3_TOPOLOGY_CHANGED")]
    public void FreshEvidenceRetryDoesNotBroadenIntoIdentityOrTopologyRecovery(string code)
    {
        var plan = ObservationAutomaticRecoveryPolicy.For(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown(code, "fault injection"));

        Assert.False(plan.IsRecoverable);
        Assert.Equal(0, plan.MaximumAttempts);
    }

    [Fact]
    public void AlternatingFreshEvidenceFaultCodesHaveExactBoundsUnderOneSessionFuse()
    {
        var session = new ObservationAutomaticRecoverySession();
        var first = session.Evaluate(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_TRUSTED_PL3_CARRY_FORWARD_STALE", "fault injection"));
        var alternateCode = session.Evaluate(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_FIELD_MOUNT_BINDING_FRAME_MISSING", "fault injection"));
        var originalCodeAgain = session.Evaluate(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_TRUSTED_PL3_CARRY_FORWARD_STALE", "fault injection"));

        Assert.True(first.ShouldRetry);
        Assert.Equal(1, first.AttemptNumber);
        Assert.Equal(1, first.TotalAttempts);
        Assert.True(alternateCode.ShouldRetry);
        Assert.False(alternateCode.Exhausted);
        Assert.Equal(1, alternateCode.AttemptNumber);
        Assert.Equal(2, alternateCode.TotalAttempts);
        Assert.False(originalCodeAgain.ShouldRetry);
        Assert.True(originalCodeAgain.Exhausted);
        Assert.Equal(2, originalCodeAgain.TotalAttempts);
    }

    [Fact]
    public void CloudRecoveryIsZeroMotionBoundedAndThenHardStops()
    {
        var session = new ObservationAutomaticRecoverySession();
        var gate = GateResult.Unknown(
            "G3_CLOUD_OR_TRANSPARENCY_INVALID",
            "fault-injected opaque frame");

        var first = session.Evaluate(ObservationStage.AcquireG3SlitField, gate);
        var second = session.Evaluate(ObservationStage.AcquireG3SlitField, gate);
        var third = session.Evaluate(ObservationStage.AcquireG3SlitField, gate);
        var exhausted = session.Evaluate(ObservationStage.AcquireG3SlitField, gate);

        Assert.True(first.ShouldRetry);
        Assert.True(second.ShouldRetry);
        Assert.True(third.ShouldRetry);
        Assert.Equal(new[] { 1, 2, 3 }, new[] { first.AttemptNumber, second.AttemptNumber, third.AttemptNumber });
        Assert.Equal(TimeSpan.FromSeconds(5), first.Plan.Delay);
        Assert.False(exhausted.ShouldRetry);
        Assert.True(exhausted.Exhausted);
        Assert.Equal(3, exhausted.TotalAttempts);
    }

    [Fact]
    public void ConnectionRecoveryNeverInheritsIdentityOrSafetyFailures()
    {
        var session = new ObservationAutomaticRecoverySession();

        var first = session.Evaluate(
            ObservationStage.ValidateNightSetup,
            GateResult.Unknown("TELESCOPE_CONNECT_FAILED", "fault injection"));
        var second = session.Evaluate(
            ObservationStage.ValidateNightSetup,
            GateResult.Unknown("TELESCOPE_CONNECT_FAILED", "fault injection"));
        Assert.True(first.ShouldRetry);
        Assert.True(second.ShouldRetry);

        var exactCodeExhausted = session.Evaluate(
            ObservationStage.ValidateNightSetup,
            GateResult.Unknown("TELESCOPE_CONNECT_FAILED", "fault injection"));
        Assert.True(exactCodeExhausted.Exhausted);

        Assert.True(session.Evaluate(
            ObservationStage.ValidateNightSetup,
            GateResult.Unknown("UVEX_AUTO_CONNECT_FAILED", "independent fault injection")).ShouldRetry);

        var identity = session.Evaluate(
            ObservationStage.ValidateNightSetup,
            GateResult.Fail("TELESCOPE_IDENTITY_MISMATCH", "fault injection"));
        var safety = session.Evaluate(
            ObservationStage.ValidateNightSetup,
            GateResult.Fail("RAIN_DETECTED", "fault injection"));
        Assert.False(identity.ShouldRetry);
        Assert.False(safety.ShouldRetry);
    }

    [Fact]
    public void ClosedLoopFaultSequenceReachesPassWithoutResettingTheSession()
    {
        var session = new ObservationAutomaticRecoverySession();
        var injected = new[]
        {
            GateResult.Unknown("G3_CLOUD_OR_TRANSPARENCY_INVALID", "cloud 1"),
            GateResult.Unknown("G3_CLOUD_OR_TRANSPARENCY_INVALID", "cloud 2"),
            GateResult.Pass("G3_FIELD_ANALYZED", "clear frame"),
        };

        var retries = 0;
        foreach (var gate in injected)
        {
            var decision = session.Evaluate(ObservationStage.AcquireG3SlitField, gate);
            if (decision.ShouldRetry)
            {
                retries++;
                continue;
            }

            Assert.Equal(GateDisposition.Passed, gate.Disposition);
            break;
        }

        Assert.Equal(2, retries);
    }

    [Fact]
    public void NestedGuideRecoveryHasIndependentExactBoundsButOneTotalFence()
    {
        var session = new ObservationAutomaticRecoverySession();

        var outer = session.Evaluate(
            ObservationStage.RunScienceBlock,
            GateResult.Unknown("GUIDING_LOST", "fault-injected lost guide epoch"));
        var nestedG3 = session.Evaluate(
            ObservationStage.AcquireG3SlitField,
            GateResult.Unknown("G3_CLOUD_OR_TRANSPARENCY_INVALID", "fault-injected cloud"));
        var nestedGuide = session.Evaluate(
            ObservationStage.StartGuiding,
            GateResult.Unknown("PHD2_PLACEMENT_SETTLE_STALE", "fault-injected stale settle"));

        Assert.True(outer.ShouldRetry);
        Assert.True(nestedG3.ShouldRetry);
        Assert.True(nestedGuide.ShouldRetry);
        Assert.Equal(new[] { 1, 1, 1 }, new[] { outer.AttemptNumber, nestedG3.AttemptNumber, nestedGuide.AttemptNumber });
        Assert.Equal(new[] { 1, 2, 3 }, new[] { outer.TotalAttempts, nestedG3.TotalAttempts, nestedGuide.TotalAttempts });
    }

    [Fact]
    public void AlternatingRecoverableCodesCannotCrossTheSessionFuse()
    {
        var session = new ObservationAutomaticRecoverySession();
        var codes = new[]
        {
            "ATR_CONNECT_FAILED",
            "TELESCOPE_CONNECT_FAILED",
            "C11_MAIN_FOCUSER_CONNECT_FAILED",
            "GUIDER_CONNECT_FAILED",
            "SAFETY_MONITOR_CONNECT_FAILED",
            "WEATHER_CONNECT_FAILED",
            "ROOF_ADAPTER_CONNECT_FAILED",
            "OPTICAL_COVER_CONNECT_FAILED",
            "UVEX_AUTO_CONNECT_FAILED",
        };

        var decisions = codes
            .Select(code => session.Evaluate(
                ObservationStage.ValidateNightSetup,
                GateResult.Unknown(code, "alternating fault injection")))
            .ToArray();

        Assert.All(decisions[..ObservationAutomaticRecoverySession.MaximumTotalAttempts], decision => Assert.True(decision.ShouldRetry));
        Assert.False(decisions[^1].ShouldRetry);
        Assert.True(decisions[^1].Exhausted);
        Assert.Equal(ObservationAutomaticRecoverySession.MaximumTotalAttempts, decisions[^1].TotalAttempts);
    }
}
