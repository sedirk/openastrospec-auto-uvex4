using System.Text.Json.Nodes;
using Xunit;

namespace UvexAdv.Observatory.Tests;

public sealed class G3AcquisitionRecoveryTests
{
    [Fact]
    public void StableNearOriginToleranceIsWiderThanStrictBindingButCappedByFreshSolveLimit()
    {
        var state = State(DateTimeOffset.UtcNow) with { ArrivalToleranceArcseconds = 2 };

        Assert.Equal(10, G3AcquisitionMotionPlanner.ComputeStableNearOriginToleranceArcseconds(state, 60));
        Assert.Equal(6, G3AcquisitionMotionPlanner.ComputeStableNearOriginToleranceArcseconds(state, 6));
        Assert.True(double.IsNaN(G3AcquisitionMotionPlanner.ComputeStableNearOriginToleranceArcseconds(state, 0)));
    }

    [Fact]
    public void ExposurePresetIsVersionedOrderedAndContainsNoUniversalFallback()
    {
        var valid = new G3PlateSolveExposurePreset(
            G3PlateSolveExposurePreset.CurrentSchemaVersion,
            "g3-solve-night-20260819-v1",
            [2_000, 5_000, 10_000]);

        Assert.Empty(valid.Validate());
        Assert.Contains("preset id", new G3PlateSolveExposurePreset(G3PlateSolveExposurePreset.CurrentSchemaVersion, "", [2_000]).Validate()[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("strictly increasing", new G3PlateSolveExposurePreset(G3PlateSolveExposurePreset.CurrentSchemaVersion, "x", [5_000, 2_000]).Validate()[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at least one", new G3PlateSolveExposurePreset(G3PlateSolveExposurePreset.CurrentSchemaVersion, "x", []).Validate()[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FreshWcsDistinguishesInsideFromSolvedButOutside()
    {
        var inside = G3SolvedFieldPolicy.TargetInsideField(500, 400, 1000, 800, 10);
        var outside = G3SolvedFieldPolicy.TargetInsideField(1005, 400, 1000, 800, 10);

        Assert.Equal(GateDisposition.Passed, inside.Disposition);
        Assert.Equal("G3_SOLVED_TARGET_OUTSIDE", outside.Code);
        Assert.Equal(GateDisposition.Indeterminate, outside.Disposition);
    }

    [Theory]
    [InlineData(10d, 20d, 10.01d, 20.02d)]
    [InlineData(359.999d, 0d, 0.001d, 0d)]
    [InlineData(359.999d, 60d, 0.001d, 60d)]
    [InlineData(15d, 89.9d, 195d, 89.9d)]
    [InlineData(123.4d, 89.999d, 123.5d, 89.9995d)]
    public void VersionedGnomonicProjectionRoundTripsAtWrapAndHighDeclination(
        double originRa,
        double originDec,
        double targetRa,
        double targetDec)
    {
        var projected = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(
            originRa,
            originDec,
            targetRa,
            targetDec);
        var restored = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            originRa,
            originDec,
            projected.RaArcseconds,
            projected.DecArcseconds);

        Assert.True(double.IsFinite(projected.RaArcseconds));
        Assert.True(double.IsFinite(projected.DecArcseconds));
        Assert.InRange(Math.Abs(Math.IEEERemainder(restored.RaDegrees - targetRa, 360)), 0, 1e-9);
        Assert.InRange(Math.Abs(restored.DecDegrees - targetDec), 0, 1e-9);
    }

    [Fact]
    public void HighDeclinationOppositeRaIsNorthwardAndReturnUsesTrueSphericalDistance()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var projected = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(15, 89.9, 195, 89.9);
        var state = State(started) with
        {
            OriginRaDegrees = 15,
            OriginDeclinationDegrees = 89.9,
            PriorReportedRaDegrees = 15,
            PriorReportedDeclinationDegrees = 89.9,
            CommandedRaDegrees = 15,
            CommandedDeclinationDegrees = 89.9,
            MaximumSingleCorrectionArcseconds = 30,
            MaximumRadiusArcseconds = 1_000,
            MaximumCumulativeMotionArcseconds = 3_000,
            MaximumCorrectionAttempts = 100,
        };

        var step = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state,
            195,
            89.9,
            2,
            started);
        var trueCommand = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            195,
            89.9,
            step.CommandedRaDegrees,
            step.CommandedDeclinationDegrees);

        Assert.InRange(Math.Abs(projected.RaArcseconds), 0, 1e-6);
        Assert.Equal(720.002924, projected.DecArcseconds, precision: 5);
        Assert.Equal(GateDisposition.Passed, step.Gate.Disposition);
        Assert.InRange(trueCommand, 27.99999, 28.00001);
        Assert.InRange(step.CommandMagnitudeArcseconds, 27.99999, 28.00001);
    }

    [Fact]
    public void ProjectionSingularityAndLegacyGeometryBindingFailClosed()
    {
        var singular = G3AcquisitionMotionPlanner.SignedTangentOffsetArcseconds(0, 0, 90, 0);
        var legacy = State(DateTimeOffset.Parse("2026-08-19T00:00:00Z")) with
        {
            SchemaVersion = 1,
            Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
            CommandMagnitudeArcseconds = 20,
            CumulativeMotionArcseconds = 22,
            CorrectionAttempts = 1,
        };
        var wrongProjection = State(DateTimeOffset.Parse("2026-08-19T00:00:00Z")) with
        {
            TangentProjectionId = "LEGACY-EQUIRECTANGULAR",
        };

        Assert.False(double.IsFinite(singular.RaArcseconds));
        Assert.False(double.IsFinite(singular.DecArcseconds));
        Assert.Contains(legacy.Validate(), issue => issue.Contains("schema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wrongProjection.Validate(), issue => issue.Contains("projection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReturnAttemptReserveUsesAdversarialWrongWayArrivalProgress()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var state = State(started) with
        {
            CurrentRaTangentOffsetArcseconds = 25,
            MaximumSingleCorrectionArcseconds = 30,
            ArrivalToleranceArcseconds = 2,
            MaximumRadiusArcseconds = 100,
            MaximumCumulativeMotionArcseconds = 500,
            MaximumCorrectionAttempts = 20,
            WorstCaseActionSeconds = 10,
            MaximumElapsedSeconds = 300,
        };

        // The proposed 28" command ends at radius 53". A legal physical
        // endpoint can be 2" farther from the origin, while every 28" return
        // command can make only 26" guaranteed radial progress. Three return
        // actions are therefore required; ceil(53/28)==2 would underreserve.
        var reserve = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
            state,
            53,
            0,
            started);

        Assert.Equal(GateDisposition.Passed, reserve.Gate.Disposition);
        Assert.Equal(3, reserve.ReservedReturnMoves);
    }

    [Fact]
    public void OutboundIsWithheldWhenWorstCaseReturnDoesNotFitElapsedEnvelope()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var state = State(started) with
        {
            CurrentRaTangentOffsetArcseconds = 25,
            MaximumSingleCorrectionArcseconds = 30,
            ArrivalToleranceArcseconds = 2,
            MaximumRadiusArcseconds = 100,
            MaximumCumulativeMotionArcseconds = 500,
            MaximumCorrectionAttempts = 20,
            WorstCaseActionSeconds = 60,
            MaximumElapsedSeconds = 200,
        };

        var reserve = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
            state,
            53,
            0,
            started.AddSeconds(30));

        Assert.Equal("G3_MOTION_RETURN_TIME_RESERVE_LIMIT", reserve.Gate.Code);
    }

    [Fact]
    public void ReturnPlannerUsesCallerTimeAndCapsCommandPlusArrivalError()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var state = State(started) with
        {
            MaximumSingleCorrectionArcseconds = 30,
            ArrivalToleranceArcseconds = 2,
            MaximumRadiusArcseconds = 100,
            MaximumCumulativeMotionArcseconds = 500,
            MaximumCorrectionAttempts = 20,
            WorstCaseActionSeconds = 20,
            MaximumElapsedSeconds = 100,
        };

        var planned = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state,
            10 + 53d / 3600d,
            20,
            2,
            started.AddSeconds(10));
        var tooLate = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state,
            10 + 53d / 3600d,
            20,
            2,
            started.AddSeconds(90));

        Assert.Equal(GateDisposition.Passed, planned.Gate.Disposition);
        Assert.Equal(28, planned.CommandMagnitudeArcseconds, precision: 9);
        Assert.True(planned.CommandMagnitudeArcseconds + state.ArrivalToleranceArcseconds <= state.MaximumSingleCorrectionArcseconds);
        Assert.Equal("G3_MOTION_RETURN_TIME_LIMIT", tooLate.Gate.Code);
    }

    [Fact]
    public async Task CanonicalEnvelopeRejectsTamperingAndDiscoveryIsRunBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-g3-recovery-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "run-a", "control", "g3-acquisition-motion.json");
        try
        {
            var state = State(DateTimeOffset.Parse("2026-08-19T00:00:00Z"));
            await G3AcquisitionMotionStore.WriteAtomicAsync(path, state);

            var loaded = await G3AcquisitionMotionStore.LoadAsync(path);
            var discovered = await G3AcquisitionMotionStore.DiscoverAsync(root);
            Assert.Null(loaded.Error);
            Assert.Equal(state, loaded.State);
            Assert.Single(discovered);
            Assert.Equal(Path.GetFullPath(path), discovered[0].Path);

            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["state"]!["lastReason"] = "tampered";
            await File.WriteAllTextAsync(path, json.ToJsonString());
            var tampered = await G3AcquisitionMotionStore.LoadAsync(path);

            Assert.Null(tampered.State);
            Assert.Contains("SHA-256", tampered.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitOperatorClearRetainsPriorLedgerAndNeverResetsConsumedBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-g3-operator-clear-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "run-a", "control", "g3-acquisition-motion.json");
        try
        {
            var started = DateTimeOffset.Parse("2026-08-27T00:00:00Z");
            var outstanding = State(started) with
            {
                Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
                CurrentRaTangentOffsetArcseconds = 300,
                CommandMagnitudeArcseconds = 300,
                MaximumSingleCorrectionArcseconds = 600,
                MaximumRadiusArcseconds = 900,
                MaximumCumulativeMotionArcseconds = 2_400,
                CumulativeMotionArcseconds = 302,
                CorrectionAttempts = 1,
                UpdatedUtc = started.AddSeconds(30),
            };
            await G3AcquisitionMotionStore.WriteAtomicAsync(path, outstanding);

            var result = await G3AcquisitionMotionStore.ReconcileOutstandingByOperatorAsync(
                path,
                started.AddMinutes(1),
                11,
                21,
                "J2000",
                "mount-a");
            var loaded = await G3AcquisitionMotionStore.LoadAsync(path);

            Assert.Null(loaded.Error);
            Assert.NotNull(loaded.State);
            Assert.Equal(G3AcquisitionMotionPhase.SettledBudgetLedger, loaded.State!.Phase);
            Assert.Equal(0, loaded.State.CommandMagnitudeArcseconds);
            Assert.Equal(outstanding.BudgetLineageId, loaded.State.BudgetLineageId);
            Assert.Equal(outstanding.OriginRaDegrees, loaded.State.OriginRaDegrees);
            Assert.Equal(outstanding.CumulativeMotionArcseconds, loaded.State.CumulativeMotionArcseconds);
            Assert.Equal(outstanding.CorrectionAttempts, loaded.State.CorrectionAttempts);
            Assert.Equal(outstanding.StartedUtc, loaded.State.StartedUtc);
            Assert.Contains("No mount command was sent", loaded.State.LastReason, StringComparison.Ordinal);
            Assert.True(File.Exists(result.PriorStateBackupPath));
            Assert.True(File.Exists(result.AuditPath));
            var retained = await G3AcquisitionMotionStore.LoadAsync(result.PriorStateBackupPath);
            Assert.Equal(outstanding, retained.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(G3AcquisitionMotionPhase.SettledBudgetLedger)]
    [InlineData(G3AcquisitionMotionPhase.AwaitingFreshSolve)]
    public async Task OperatorRetirementRemovesCanonicalDiscoveryForSettledAndOutstandingLedgers(
        G3AcquisitionMotionPhase phase)
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-g3-operator-retire-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "run-a", "control", "g3-acquisition-motion.json");
        try
        {
            var started = DateTimeOffset.Parse("2026-08-27T00:00:00Z");
            var prior = State(started) with
            {
                Phase = phase,
                CurrentRaTangentOffsetArcseconds = phase == G3AcquisitionMotionPhase.SettledBudgetLedger ? 0 : 300,
                CommandMagnitudeArcseconds = phase == G3AcquisitionMotionPhase.SettledBudgetLedger ? 0 : 300,
                CumulativeMotionArcseconds = phase == G3AcquisitionMotionPhase.SettledBudgetLedger ? 0 : 300,
                CorrectionAttempts = phase == G3AcquisitionMotionPhase.SettledBudgetLedger ? 0 : 1,
                MaximumSingleCorrectionArcseconds = 600,
                MaximumRadiusArcseconds = 900,
                MaximumCumulativeMotionArcseconds = 2_400,
                UpdatedUtc = started.AddSeconds(30),
            };
            await G3AcquisitionMotionStore.WriteAtomicAsync(path, prior);

            var result = await G3AcquisitionMotionStore.RetireByOperatorAsync(
                path,
                started.AddMinutes(1),
                11,
                21,
                "J2000",
                "mount-a");
            var discovered = await G3AcquisitionMotionStore.DiscoverAsync(root);
            var retained = await G3AcquisitionMotionStore.LoadAsync(result.PriorStateBackupPath);

            Assert.False(File.Exists(path));
            Assert.Empty(discovered);
            Assert.True(File.Exists(result.PriorStateBackupPath));
            Assert.True(File.Exists(result.AuditPath));
            Assert.Equal(prior, retained.State);
            Assert.Equal(G3AcquisitionMotionPhase.SettledBudgetLedger, result.RetiredState.Phase);
            Assert.Equal(0, result.RetiredState.CommandMagnitudeArcseconds);
            Assert.Contains("No mount command was sent", result.RetiredState.LastReason, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OutstandingIntentMustBePrecharged()
    {
        var state = State(DateTimeOffset.Parse("2026-08-19T00:00:00Z")) with
        {
            Phase = G3AcquisitionMotionPhase.OutboundIntent,
            CommandMagnitudeArcseconds = 20,
            CorrectionAttempts = 0,
            CumulativeMotionArcseconds = 0,
        };

        var issues = state.Validate();

        Assert.Contains(issues, issue => issue.Contains("precharge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SettledCrossRunContinuationCannotResetLineageBudgetOrClock()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var prior = State(started) with
        {
            CumulativeMotionArcseconds = 137,
            CorrectionAttempts = 7,
            UpdatedUtc = started.AddMinutes(2),
        };

        var continued = G3AcquisitionMotionPlanner.ContinueSettledLedger(
            prior,
            "run-b",
            G3AcquisitionMotionKind.LocalSearch,
            "evidence/run-b-g3-wcs.json",
            started.AddMinutes(3),
            familyMaximumSingleCorrectionArcseconds: 6,
            familyMaximumRadiusArcseconds: 20,
            familyAdditionalCumulativeMotionArcseconds: 40,
            familyAdditionalCorrectionAttempts: 3,
            familyAdditionalElapsedTime: TimeSpan.FromMinutes(1));

        Assert.Equal(prior.BudgetLineageId, continued.BudgetLineageId);
        Assert.Equal(prior.StartedUtc, continued.StartedUtc);
        Assert.Equal(prior.CumulativeMotionArcseconds, continued.CumulativeMotionArcseconds);
        Assert.Equal(prior.CorrectionAttempts, continued.CorrectionAttempts);
        Assert.Equal(177, continued.MaximumCumulativeMotionArcseconds);
        Assert.Equal(10, continued.MaximumCorrectionAttempts);
        Assert.Equal(240, continued.MaximumElapsedSeconds);
        Assert.Equal(6, continued.MaximumSingleCorrectionArcseconds);
        Assert.Equal(20, continued.MaximumRadiusArcseconds);
        Assert.Equal("run-b", continued.ObservationRunId);
        Assert.Equal(G3AcquisitionMotionKind.LocalSearch, continued.Kind);
    }

    [Fact]
    public void ReattestedCommissioningCeilingAllowsFamilyIncrementWithoutResettingConsumption()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var prior = State(started) with
        {
            MaximumCumulativeMotionArcseconds = 150,
            MaximumCorrectionAttempts = 8,
            MaximumElapsedSeconds = 180,
            CumulativeMotionArcseconds = 137,
            CorrectionAttempts = 7,
            UpdatedUtc = started.AddMinutes(2),
        };

        var continued = G3AcquisitionMotionPlanner.ContinueSettledLedger(
            prior,
            "run-b",
            G3AcquisitionMotionKind.LocalSearch,
            "evidence/run-b-g3-search.json",
            started.AddMinutes(3),
            familyMaximumSingleCorrectionArcseconds: 6,
            familyMaximumRadiusArcseconds: 20,
            familyAdditionalCumulativeMotionArcseconds: 40,
            familyAdditionalCorrectionAttempts: 3,
            familyAdditionalElapsedTime: TimeSpan.FromMinutes(1),
            attestedLineageMaximumCumulativeMotionArcseconds: 500,
            attestedLineageMaximumCorrectionAttempts: 20,
            attestedLineageMaximumElapsedTime: TimeSpan.FromMinutes(5));

        Assert.Equal(prior.BudgetLineageId, continued.BudgetLineageId);
        Assert.Equal(prior.StartedUtc, continued.StartedUtc);
        Assert.Equal(137, continued.CumulativeMotionArcseconds);
        Assert.Equal(7, continued.CorrectionAttempts);
        Assert.Equal(177, continued.MaximumCumulativeMotionArcseconds);
        Assert.Equal(10, continued.MaximumCorrectionAttempts);
        Assert.Equal(240, continued.MaximumElapsedSeconds);
    }

    [Fact]
    public void NominalSearchStepAllowsPriorArrivalErrorAndReservesNextArrivalError()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var state = State(started) with
        {
            CurrentRaTangentOffsetArcseconds = -2,
            MaximumSingleCorrectionArcseconds = 304,
            ArrivalToleranceArcseconds = 2,
            MaximumRadiusArcseconds = 900,
            MaximumCumulativeMotionArcseconds = 3_600,
            MaximumCorrectionAttempts = 12,
            WorstCaseActionSeconds = 10,
            MaximumElapsedSeconds = 1_200,
        };

        var reserve = G3AcquisitionMotionPlanner.ValidateOutboundAndReturnReserve(
            state,
            300,
            0,
            started);

        Assert.Equal(GateDisposition.Passed, reserve.Gate.Disposition);
        Assert.InRange(reserve.MoveFromCurrentArcseconds, 301.99, 302.01);
        Assert.Equal(1, reserve.ReservedReturnMoves);
    }

    private static G3AcquisitionMotionState State(DateTimeOffset started) => new(
        SchemaVersion: G3AcquisitionMotionState.CurrentSchemaVersion,
        TangentProjectionId: G3AcquisitionMotionState.CurrentTangentProjectionId,
        ObservationRunId: "run-a",
        BudgetLineageId: Guid.Parse("6e7fa5f2-6be9-4090-8825-4a91b94d9d65").ToString("N"),
        ActionConfigurationSha256: new string('A', 64),
        RecoveryContextSha256: new string('B', 64),
        CommissioningPresetSha256: new string('C', 64),
        Kind: G3AcquisitionMotionKind.LocalSearch,
        Phase: G3AcquisitionMotionPhase.SettledBudgetLedger,
        PierSide: "pierEast",
        CoordinateEpoch: "J2000",
        OriginRaDegrees: 10,
        OriginDeclinationDegrees: 20,
        PriorReportedRaDegrees: 10,
        PriorReportedDeclinationDegrees: 20,
        CommandedRaDegrees: 10,
        CommandedDeclinationDegrees: 20,
        CurrentRaTangentOffsetArcseconds: 0,
        CurrentDeclinationOffsetArcseconds: 0,
        CommandMagnitudeArcseconds: 0,
        MaximumSingleCorrectionArcseconds: 30,
        MaximumRadiusArcseconds: 100,
        MaximumCumulativeMotionArcseconds: 500,
        MaximumCorrectionAttempts: 20,
        ArrivalToleranceArcseconds: 2,
        WorstCaseActionSeconds: 10,
        MaximumElapsedSeconds: 300,
        CumulativeMotionArcseconds: 0,
        CorrectionAttempts: 0,
        StartedUtc: started,
        CreatedUtc: started,
        UpdatedUtc: started,
        DeclaredEvidencePath: "evidence/g3-search-declared.json",
        LastReason: "test");
}
