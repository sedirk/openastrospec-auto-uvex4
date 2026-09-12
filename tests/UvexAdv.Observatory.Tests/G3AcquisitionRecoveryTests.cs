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
    public void FinalOriginCommandAndStrictIntermediateShareOneFullyChargedSegment()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        const double finalStableToleranceArcseconds = 10;
        var state = State(started) with
        {
            MaximumSingleCorrectionArcseconds = 310,
            ArrivalToleranceArcseconds = 2,
            MaximumRadiusArcseconds = 1_000,
            MaximumCumulativeMotionArcseconds = 2_000,
            MaximumCorrectionAttempts = 20,
        };
        var directReported = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            300,
            0);
        var stagedReported = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            305,
            0);
        var outsideRadiusReported = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            state.MaximumRadiusArcseconds + state.ArrivalToleranceArcseconds + 1,
            0);

        var direct = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state,
            directReported.RaDegrees,
            directReported.DecDegrees,
            finalStableToleranceArcseconds,
            started);
        var staged = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state,
            stagedReported.RaDegrees,
            stagedReported.DecDegrees,
            finalStableToleranceArcseconds,
            started);
        var outsideRadius = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state,
            outsideRadiusReported.RaDegrees,
            outsideRadiusReported.DecDegrees,
            finalStableToleranceArcseconds,
            started);

        var directEndpointRadius = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            direct.CommandedRaDegrees,
            direct.CommandedDeclinationDegrees);
        var stagedEndpointRadius = G3AcquisitionMotionPlanner.AngularSeparationArcseconds(
            state.OriginRaDegrees,
            state.OriginDeclinationDegrees,
            staged.CommandedRaDegrees,
            staged.CommandedDeclinationDegrees);

        Assert.Equal(GateDisposition.Passed, direct.Gate.Disposition);
        Assert.InRange(direct.CommandMagnitudeArcseconds, 299.999, 300.001);
        Assert.InRange(directEndpointRadius, 0, 0.001);
        Assert.True(
            direct.CommandMagnitudeArcseconds + finalStableToleranceArcseconds <=
            state.MaximumSingleCorrectionArcseconds + 1e-9);

        Assert.Equal(GateDisposition.Passed, staged.Gate.Disposition);
        Assert.InRange(staged.CommandMagnitudeArcseconds, 296.999, 297.001);
        Assert.InRange(stagedEndpointRadius, 7.999, 8.001);
        Assert.True(stagedEndpointRadius > 0);
        Assert.True(
            staged.CommandMagnitudeArcseconds + state.ArrivalToleranceArcseconds <=
            state.MaximumSingleCorrectionArcseconds + 1e-9);
        Assert.Equal("G3_MOTION_RETURN_OUTSIDE_RADIUS", outsideRadius.Gate.Code);
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
            Assert.DoesNotContain("failedNeighbourApproaches", await File.ReadAllTextAsync(path));

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
    public async Task FailedNeighbourMemoryRoundTripsWithoutReissuingAnyBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-g3-neighbour-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "control", "g3-acquisition-motion.json");
        try
        {
            var original = State(DateTimeOffset.Parse("2026-08-19T00:00:00Z")) with
            {
                CorrectionAttempts = 1,
                CumulativeMotionArcseconds = 30,
            };
            var retained = original with { FailedNeighbourApproaches = 1 };
            Assert.Empty(retained.Validate());
            await G3AcquisitionMotionStore.WriteAtomicAsync(path, retained);
            var loaded = await G3AcquisitionMotionStore.LoadAsync(path);
            Assert.Null(loaded.Error);
            Assert.Equal(retained, loaded.State);
            Assert.Equal(original, loaded.State! with { FailedNeighbourApproaches = 0 });
            Assert.Contains("failedNeighbourApproaches", await File.ReadAllTextAsync(path));
            Assert.NotEmpty((original with { FailedNeighbourApproaches = -1 }).Validate());
            Assert.NotEmpty((original with { FailedNeighbourApproaches = original.CorrectionAttempts + 1 }).Validate());
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
    public void LargerWcsHandoffCannotPriceAnAlreadyReservedSearchReturnOutOfItsBudget()
    {
        var started = DateTimeOffset.UtcNow;
        var prior = State(started) with
        {
            MaximumSingleCorrectionArcseconds = 420,
            MaximumRadiusArcseconds = 900,
            MaximumCumulativeMotionArcseconds = 21600,
            MaximumCorrectionAttempts = 12,
            MaximumElapsedSeconds = 1200,
            CurrentRaTangentOffsetArcseconds = 300,
            CumulativeMotionArcseconds = 18631.22771520692,
            CorrectionAttempts = 9,
        };
        var continued = G3AcquisitionMotionPlanner.ContinueSettledLedger(prior, "run-a",
            G3AcquisitionMotionKind.WcsCentering, "evidence/wcs.json", started.AddSeconds(10),
            familyMaximumSingleCorrectionArcseconds: 5400,
            familyMaximumRadiusArcseconds: 18000,
            attestedLineageMaximumSingleCorrectionArcseconds: 5400,
            attestedLineageMaximumRadiusArcseconds: 18000);
        Assert.Equal(420, continued.MaximumSingleCorrectionArcseconds);
        Assert.Equal(prior.CumulativeMotionArcseconds, continued.CumulativeMotionArcseconds);
        Assert.Equal(prior.CorrectionAttempts, continued.CorrectionAttempts);
        Assert.Equal(prior.StartedUtc, continued.StartedUtc);
        Assert.True(continued.CumulativeMotionArcseconds + continued.MaximumSingleCorrectionArcseconds < continued.MaximumCumulativeMotionArcseconds);
        var point = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(prior.OriginRaDegrees, prior.OriginDeclinationDegrees, 300, 0);
        var next = G3AcquisitionMotionPlanner.PlanNextReturnStep(continued, point.RaDegrees, point.DecDegrees, 10, started.AddSeconds(10));
        Assert.Equal(GateDisposition.Passed, next.Gate.Disposition);
        Assert.Empty(continued.Validate());
    }

    [Fact]
    public void ReattestedLargeWcsFamilyIsNotPermanentlyTruncatedByPriorLocalSearchLimits()
    {
        var started = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
        var prior = State(started) with
        {
            MaximumSingleCorrectionArcseconds = 304,
            MaximumRadiusArcseconds = 900,
            MaximumCumulativeMotionArcseconds = 3_600,
            MaximumCorrectionAttempts = 12,
            MaximumElapsedSeconds = 1_200,
            CumulativeMotionArcseconds = 302,
            CorrectionAttempts = 1,
            UpdatedUtc = started.AddMinutes(1),
        };

        var continued = G3AcquisitionMotionPlanner.ContinueSettledLedger(
            prior,
            "run-a",
            G3AcquisitionMotionKind.WcsCentering,
            "evidence/run-a-g3-wcs.json",
            started.AddMinutes(2),
            familyMaximumSingleCorrectionArcseconds: 5_400,
            familyMaximumRadiusArcseconds: 18_000,
            familyAdditionalCumulativeMotionArcseconds: 21_600,
            familyAdditionalCorrectionAttempts: 8,
            familyAdditionalElapsedTime: TimeSpan.FromMinutes(20),
            attestedLineageMaximumSingleCorrectionArcseconds: 5_400,
            attestedLineageMaximumRadiusArcseconds: 18_000,
            attestedLineageMaximumCumulativeMotionArcseconds: 21_600,
            attestedLineageMaximumCorrectionAttempts: 12,
            attestedLineageMaximumElapsedTime: TimeSpan.FromMinutes(20));

        Assert.Equal(5_400, continued.MaximumSingleCorrectionArcseconds);
        Assert.Equal(18_000, continued.MaximumRadiusArcseconds);
        Assert.Equal(21_600, continued.MaximumCumulativeMotionArcseconds);
        Assert.Equal(9, continued.MaximumCorrectionAttempts);
        Assert.Equal(1_200, continued.MaximumElapsedSeconds);
        Assert.Equal(prior.CumulativeMotionArcseconds, continued.CumulativeMotionArcseconds);
        Assert.Equal(prior.CorrectionAttempts, continued.CorrectionAttempts);
        Assert.Equal(prior.BudgetLineageId, continued.BudgetLineageId);
        Assert.Equal(prior.OriginRaDegrees, continued.OriginRaDegrees);
        Assert.Equal(prior.OriginDeclinationDegrees, continued.OriginDeclinationDegrees);
        Assert.Equal(prior.StartedUtc, continued.StartedUtc);
        Assert.Empty(continued.Validate());
    }

    [Fact]
    public void LocalSearchCannotShrinkRadiusBelowAlreadySettledWcsEndpoint()
    {
        var started = DateTimeOffset.Parse("2026-09-05T12:00:00Z");
        var prior = State(started) with
        {
            Kind = G3AcquisitionMotionKind.WcsCentering,
            MaximumSingleCorrectionArcseconds = 5_400,
            MaximumRadiusArcseconds = 18_000,
            MaximumCumulativeMotionArcseconds = 21_600,
            MaximumCorrectionAttempts = 12,
            MaximumElapsedSeconds = 1_200,
            CurrentRaTangentOffsetArcseconds = 10_000,
            CumulativeMotionArcseconds = 10_046,
            CorrectionAttempts = 2,
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            G3AcquisitionMotionPlanner.ContinueSettledLedger(
                prior, "run-b", G3AcquisitionMotionKind.LocalSearch,
                "evidence/search.json", started.AddMinutes(5),
                familyMaximumSingleCorrectionArcseconds: 310,
                familyMaximumRadiusArcseconds: 900));

        Assert.Contains("G3_MOTION_FAMILY_RADIUS_INCOMPATIBLE", error.Message);
        Assert.Equal(G3AcquisitionMotionPhase.SettledBudgetLedger, prior.Phase);
        Assert.Equal(18_000, prior.MaximumRadiusArcseconds);
        Assert.Equal(10_046, prior.CumulativeMotionArcseconds);
        Assert.Equal(started, prior.StartedUtc);
    }

    [Theory]
    [InlineData(2.24, 0.3, 50, 100, true)]
    [InlineData(4.01, 0.3, 50, 100, false)]
    [InlineData(2.24, 2.01, 50, 100, false)]
    [InlineData(2.24, 0.3, 101, 100, false)]
    [InlineData(2.24, 0.3, 50, 1, false)]
    public void IntermediateReturnReplanRequiresBoundedStableMeasuredProgress(
        double residual, double drift, double move, double priorRadius, bool expected)
    {
        var state = State(DateTimeOffset.UtcNow) with
        {
            Phase = G3AcquisitionMotionPhase.ReturnIntent,
            CommandMagnitudeArcseconds = 50,
            MaximumSingleCorrectionArcseconds = 100,
            MaximumRadiusArcseconds = 200,
            MaximumCumulativeMotionArcseconds = 500,
            ArrivalToleranceArcseconds = 2,
            CumulativeMotionArcseconds = 100,
            CorrectionAttempts = 1,
        };
        Assert.Equal(expected, G3AcquisitionMotionPlanner.CanReplanStableIntermediateReturn(
            state, priorRadius, residual, drift, move));
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

    [Fact]
    public void ExpiredReturnClockAllowsOnlyAlreadyObservedOriginNotAnotherMove()
    {
        var started = DateTimeOffset.Parse("2026-09-05T12:00:00Z");
        var state = State(started) with
        {
            Phase = G3AcquisitionMotionPhase.ReturnIntent,
            CommandMagnitudeArcseconds = 20,
            CumulativeMotionArcseconds = 30,
            CorrectionAttempts = 1,
        };
        var later = started.AddSeconds(state.MaximumElapsedSeconds + 60);
        var origin = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state, state.OriginRaDegrees, state.OriginDeclinationDegrees, 2, later);
        var offset = G3AcquisitionMotionPlanner.ApplyTangentOffsetArcseconds(
            state.OriginRaDegrees, state.OriginDeclinationDegrees, 15, 0);
        var away = G3AcquisitionMotionPlanner.PlanNextReturnStep(
            state, offset.RaDegrees, offset.DecDegrees, 2, later);

        Assert.True(origin.AlreadyAtOrigin);
        Assert.Equal(0, origin.CommandMagnitudeArcseconds);
        Assert.Equal("G3_MOTION_RETURN_TIME_LIMIT", away.Gate.Code);
        Assert.False(away.AlreadyAtOrigin);
        Assert.Equal(started, state.StartedUtc);
        Assert.Equal(30, state.CumulativeMotionArcseconds);
    }

    [Fact]
    public void WorkTimeFenceLeavesInitialAndSettledAcquisitionUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(G3AcquisitionReturnTimePolicy.Plan(null, now).OperationTimeout);
        Assert.Null(G3AcquisitionReturnTimePolicy.Plan(State(now.AddMinutes(-10)), now).OperationTimeout);
    }

    [Fact]
    public void EachCaptureAndSolveReservesSegmentedReturnBeforeStarting()
    {
        var now = DateTimeOffset.UtcNow;
        var state = State(now) with
        {
            Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve,
            CurrentRaTangentOffsetArcseconds = 60, CommandMagnitudeArcseconds = 20,
            CorrectionAttempts = 3, CumulativeMotionArcseconds = 70,
        };
        var allowed = G3AcquisitionReturnTimePolicy.Plan(state, now.AddSeconds(229));
        Assert.Equal(GateDisposition.Passed, allowed.Gate.Disposition);
        Assert.Equal(3, allowed.Gate.Metrics!["returnSegments"]);
        Assert.Equal(TimeSpan.FromSeconds(10), allowed.OperationTimeout);
        var reserved = G3AcquisitionReturnTimePolicy.Plan(state, now.AddSeconds(231));
        Assert.Equal(G3AcquisitionReturnTimePolicy.ReserveCode, reserved.Gate.Code);
        Assert.Null(reserved.OperationTimeout);
        Assert.Equal(70, state.CumulativeMotionArcseconds);
        Assert.Equal(3, state.CorrectionAttempts);
        Assert.Equal(now, state.StartedUtc);
    }

    [Theory]
    [InlineData(G3AcquisitionMotionPhase.OutboundIntent)]
    [InlineData(G3AcquisitionMotionPhase.ReturnIntent)]
    public void WorkTimeFenceDoesNotAuthorizeWorkDuringUnconfirmedMotion(G3AcquisitionMotionPhase phase)
    {
        var now = DateTimeOffset.UtcNow;
        var state = State(now) with { Phase = phase, CommandMagnitudeArcseconds = 10, CumulativeMotionArcseconds = 10, CorrectionAttempts = 1 };
        Assert.Equal("G3_WORK_RETURN_STATE_INVALID", G3AcquisitionReturnTimePolicy.Plan(state, now).Gate.Code);
    }

    [Fact]
    public async Task ReservedTimeStartsNoWorkAndDoesNotResetAnyBudget()
    {
        var called = false;
        var plan = new G3AcquisitionWorkTimePlan(GateResult.Unknown(G3AcquisitionReturnTimePolicy.ReserveCode, "return first"), null);
        var ex = await Assert.ThrowsAsync<G3ReturnTimeReserveException>(() => G3AcquisitionReturnTimePolicy.ExecuteAsync(plan,
            _ => { called = true; return Task.FromResult(1); }, CancellationToken.None));
        Assert.False(called);
        Assert.False(ex.OperationStarted);
    }

    [Fact]
    public async Task TimeFenceWaitsForOwnerCleanupBeforeReportingReturnRequired()
    {
        var cleaned = false;
        var plan = new G3AcquisitionWorkTimePlan(GateResult.Pass("reserved", "bounded"), TimeSpan.FromMilliseconds(20));
        var ex = await Assert.ThrowsAsync<G3ReturnTimeReserveException>(() => G3AcquisitionReturnTimePolicy.ExecuteAsync(plan,
            async token =>
            {
                try { await Task.Delay(10000, token); return 1; }
                finally { await Task.Yield(); cleaned = true; }
            }, CancellationToken.None));
        Assert.True(cleaned);
        Assert.True(ex.OperationStarted);
        Assert.Equal(G3AcquisitionReturnTimePolicy.ReserveCode, ex.Gate.Code);
    }

    [Fact]
    public async Task CallerCancellationAndIndependentOwnerCancellationAreNotReclassified()
    {
        var plan = new G3AcquisitionWorkTimePlan(GateResult.Pass("reserved", "bounded"), TimeSpan.FromSeconds(60));
        using var caller = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => G3AcquisitionReturnTimePolicy.ExecuteAsync<int>(plan,
            token => { caller.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(1); }, caller.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => G3AcquisitionReturnTimePolicy.ExecuteAsync<int>(plan,
            _ => throw new OperationCanceledException("independent owner cancellation"), CancellationToken.None));
    }

    [Fact]
    public async Task PriorCrossPierFinalReturnClosesOnlyItsOriginalLedgerAndRetainsAccounting()
    {
        var state = PriorFinalReturn();
        var samples = OriginSamples(state);
        var closed = G3PriorReturnOriginPolicy.CloseVerifiedReturn(state, "new-run",
            ObservationStage.ValidateNightSetup, samples, 60, 2, 2, samples[^1].CapturedUtc);
        Assert.Equal(G3AcquisitionMotionPhase.SettledBudgetLedger, closed.Phase);
        Assert.Equal(state.ObservationRunId, closed.ObservationRunId);
        Assert.Equal(state.PierSide, closed.PierSide);
        Assert.Equal(state.StartedUtc, closed.StartedUtc);
        Assert.Equal(state.BudgetLineageId, closed.BudgetLineageId);
        Assert.Equal(state.CorrectionAttempts, closed.CorrectionAttempts);
        Assert.Equal(state.CumulativeMotionArcseconds, closed.CumulativeMotionArcseconds);
        Assert.Equal(state.MaximumElapsedSeconds, closed.MaximumElapsedSeconds);
        Assert.Equal(state.OriginRaDegrees, closed.OriginRaDegrees);
        Assert.Equal(state.OriginDeclinationDegrees, closed.OriginDeclinationDegrees);
        Assert.InRange(closed.CurrentRadiusArcseconds, 3.87, 3.90);
        Assert.Empty(closed.Validate());
        var directory = Path.Combine(Path.GetTempPath(), "g3-origin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "g3-acquisition-motion.json");
            await G3AcquisitionMotionStore.WriteAtomicAsync(path, closed);
            var read = await G3AcquisitionMotionStore.LoadAsync(path);
            Assert.Null(read.Error);
            Assert.Equal(closed, read.State);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("same-run")]
    [InlineData("current-stage")]
    [InlineData("outbound")]
    [InlineData("awaiting-solve")]
    [InlineData("intermediate-return")]
    [InlineData("same-side")]
    [InlineData("unknown-side")]
    [InlineData("epoch")]
    [InlineData("moving")]
    [InlineData("far-origin")]
    [InlineData("drift")]
    [InlineData("flip-during-window")]
    [InlineData("nonfinite")]
    [InlineData("stale")]
    [InlineData("insufficient-window")]
    [InlineData("duplicate-timestamp")]
    [InlineData("missing-samples")]
    public void PriorOriginReadbackNeverAuthorizesUnverifiedOrCurrentRunRecovery(string failure)
    {
        var state = PriorFinalReturn();
        var samples = OriginSamples(state);
        var run = "new-run";
        var stage = ObservationStage.ValidateNightSetup;
        var now = samples[^1].CapturedUtc;
        switch (failure)
        {
            case "same-run": run = state.ObservationRunId; break;
            case "current-stage": stage = ObservationStage.AcquireG3SlitField; break;
            case "outbound": state = state with { Phase = G3AcquisitionMotionPhase.OutboundIntent }; break;
            case "awaiting-solve": state = state with { Phase = G3AcquisitionMotionPhase.AwaitingFreshSolve }; break;
            case "intermediate-return": state = state with { CommandedRaDegrees = state.OriginRaDegrees + .01 }; break;
            case "same-side": samples[0] = samples[0] with { PierSide = state.PierSide }; break;
            case "unknown-side": samples[0] = samples[0] with { PierSide = "pierUnknown" }; break;
            case "epoch": samples[1] = samples[1] with { CoordinateEpoch = "J2000" }; break;
            case "moving": samples[1] = samples[1] with { ConnectedAndIdle = false }; break;
            case "far-origin": samples = samples.Select(s => s with { DeclinationDegrees = s.DeclinationDegrees + .1 }).ToArray(); break;
            case "drift": samples[1] = samples[1] with { DeclinationDegrees = samples[1].DeclinationDegrees + 3d / 3600 }; break;
            case "flip-during-window": samples[1] = samples[1] with { PierSide = state.PierSide }; break;
            case "nonfinite": samples[1] = samples[1] with { RaDegrees = double.NaN }; break;
            case "stale": now = now.AddSeconds(3); break;
            case "insufficient-window": samples[2] = samples[2] with { CapturedUtc = samples[1].CapturedUtc.AddMilliseconds(100) }; break;
            case "duplicate-timestamp": samples[1] = samples[1] with { CapturedUtc = samples[0].CapturedUtc }; break;
            case "missing-samples": samples = samples.Take(2).ToArray(); break;
        }
        var result = G3PriorReturnOriginPolicy.Evaluate(state, run, stage, samples, 60, 2, 2, now);
        Assert.NotEqual(GateDisposition.Passed, result.Disposition);
        Assert.Throws<InvalidOperationException>(() => G3PriorReturnOriginPolicy.CloseVerifiedReturn(
            state, run, stage, samples, 60, 2, 2, now));
    }

    private static G3AcquisitionMotionState PriorFinalReturn() => State(DateTimeOffset.UtcNow.AddHours(-1)) with
    {
        Kind = G3AcquisitionMotionKind.WcsCentering,
        Phase = G3AcquisitionMotionPhase.ReturnIntent,
        PierSide = "pierWest", CoordinateEpoch = "JNOW",
        OriginRaDegrees = 31.39937583333335, OriginDeclinationDegrees = 42.45742583333334,
        CommandedRaDegrees = 31.39937583333335, CommandedDeclinationDegrees = 42.45742583333334,
        PriorReportedRaDegrees = 31.547025416666656, PriorReportedDeclinationDegrees = 42.33662833333334,
        CommandMagnitudeArcseconds = 585.830258,
        MaximumSingleCorrectionArcseconds = 5400, MaximumRadiusArcseconds = 18000,
        MaximumCumulativeMotionArcseconds = 21600, MaximumCorrectionAttempts = 12,
        CumulativeMotionArcseconds = 12056.7474265, CorrectionAttempts = 9,
        WorstCaseActionSeconds = 90, MaximumElapsedSeconds = 1200,
    };

    private static G3OriginReadback[] OriginSamples(G3AcquisitionMotionState state) =>
        Enumerable.Range(0, 3).Select(i => new G3OriginReadback(state.UpdatedUtc.AddMinutes(30).AddSeconds(i),
            31.39791458333, 42.45742138888889, "JNOW", "pierEast", true)).ToArray();

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
