using UvexAdv.Observatory;
using Xunit;
using System.Text.Json.Nodes;

namespace UvexAdv.Observatory.Tests;

public sealed class SlitPlacementRecoveryTests
{
    [Fact]
    public void DurableStateRejectsUndefinedPhaseAndNonMonotonicBudget()
    {
        var issues = Pending() with
        {
            Phase = (SlitPlacementPendingPhase)999,
            CumulativeCorrectionDegrees = 1d / 3600,
        };

        var validation = issues.Validate();

        Assert.Contains(validation, issue => issue.Contains("phase", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(validation, issue => issue.Contains("cumulative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DurableStateRequiresCanonicalBudgetLineageId()
    {
        var validation = (Pending() with { BudgetLineageId = "not-a-lineage" }).Validate();

        Assert.Contains(validation, issue => issue.Contains("lineage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LineageResolverInheritsDominantNonTerminalLedgerAcrossRuns()
    {
        var lineage = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.Parse("2026-08-18T11:55:00Z");
        var old = Pending() with
        {
            ObservationRunId = "old-run",
            BudgetLineageId = lineage,
            Phase = SlitPlacementPendingPhase.SettledBudgetLedger,
            FineAcquisitionStartedUtc = started,
            CumulativeCorrectionDegrees = 60d / 3600,
            CorrectionAttempts = 2,
        };
        var newer = old with
        {
            ObservationRunId = "newer-run",
            CumulativeCorrectionDegrees = 90d / 3600,
            CorrectionAttempts = 3,
            UpdatedUtc = old.UpdatedUtc.AddMinutes(1),
        };

        var selection = SlitPlacementBudgetLineageResolver.Resolve(
            [
                new("old", old, RunIsTerminal: false),
                new("newer", newer, RunIsTerminal: false),
            ],
            currentRunId: "restart-run");

        Assert.Equal(GateDisposition.Passed, selection.Gate.Disposition);
        Assert.Equal("newer-run", selection.Candidate!.State.ObservationRunId);
        Assert.Equal(90d / 3600, selection.Candidate.State.CumulativeCorrectionDegrees, 12);
        Assert.Equal(3, selection.Candidate.State.CorrectionAttempts);
        Assert.Equal(started, selection.Candidate.State.FineAcquisitionStartedUtc);
    }

    [Fact]
    public void LineageResolverClosesSettledHistoryOnlyWhenManifestIsTerminal()
    {
        var state = Pending() with { Phase = SlitPlacementPendingPhase.SettledBudgetLedger };

        var selection = SlitPlacementBudgetLineageResolver.Resolve(
            [new("completed", state, RunIsTerminal: true)],
            currentRunId: "fresh-run");

        Assert.Equal(GateDisposition.Passed, selection.Gate.Disposition);
        Assert.Null(selection.Candidate);
        Assert.Equal("SLIT_BUDGET_LINEAGES_CLOSED", selection.Gate.Code);
    }

    [Fact]
    public void LineageResolverRejectsDivergentCountersAndMultipleLineages()
    {
        var first = Pending() with { Phase = SlitPlacementPendingPhase.SettledBudgetLedger };
        var divergent = first with
        {
            ObservationRunId = "branch",
            CumulativeCorrectionDegrees = 60d / 3600,
            CorrectionAttempts = 1,
        };
        var attemptsBranch = first with
        {
            ObservationRunId = "attempts-branch",
            CumulativeCorrectionDegrees = 30d / 3600,
            CorrectionAttempts = 2,
        };
        var otherLineage = first with
        {
            ObservationRunId = "other",
            BudgetLineageId = Guid.NewGuid().ToString("N"),
        };
        var changedBinding = first with
        {
            ObservationRunId = "changed-binding",
            ActionConfigurationSha256 = new string('d', 64),
        };

        var divergentResult = SlitPlacementBudgetLineageResolver.Resolve(
            [
                new("distance", divergent, RunIsTerminal: false),
                new("attempts", attemptsBranch, RunIsTerminal: false),
            ],
            currentRunId: "restart-run");
        var multipleResult = SlitPlacementBudgetLineageResolver.Resolve(
            [
                new("first", first, RunIsTerminal: false),
                new("other", otherLineage, RunIsTerminal: false),
            ],
            currentRunId: "restart-run");
        var bindingResult = SlitPlacementBudgetLineageResolver.Resolve(
            [
                new("first", first, RunIsTerminal: false),
                new("changed-binding", changedBinding, RunIsTerminal: false),
            ],
            currentRunId: "restart-run");

        Assert.Equal("SLIT_BUDGET_LINEAGE_COUNTERS_DIVERGED", divergentResult.Gate.Code);
        Assert.Equal("SLIT_BUDGET_MULTIPLE_ACTIVE_LINEAGES", multipleResult.Gate.Code);
        Assert.Equal("SLIT_BUDGET_LINEAGE_BINDINGS_DIVERGED", bindingResult.Gate.Code);
    }

    [Fact]
    public void LineageResolverRejectsTerminalRunWithOutstandingMotion()
    {
        var outstanding = Pending() with { Phase = SlitPlacementPendingPhase.ReturnRequired };

        var selection = SlitPlacementBudgetLineageResolver.Resolve(
            [new("terminal-pending", outstanding, RunIsTerminal: true)],
            currentRunId: "fresh-run");

        Assert.Equal(GateDisposition.Indeterminate, selection.Gate.Disposition);
        Assert.Equal("SLIT_PENDING_TERMINAL_RUN_OUTSTANDING", selection.Gate.Code);
    }

    [Fact]
    public void OutboundRequiresOneFailureReturnInsideBothBudgets()
    {
        var limits = new MotionLimits(30d / 3600, 120d / 3600, 4);

        var passed = SlitPlacementRecoveryPlanner.ValidateOutboundAndReturnReserve(
            limits, 30d / 3600, attempts: 1, segmentMagnitudeDegrees: 30d / 3600);
        var attemptBlocked = SlitPlacementRecoveryPlanner.ValidateOutboundAndReturnReserve(
            limits, 60d / 3600, attempts: 3, segmentMagnitudeDegrees: 30d / 3600);
        var cumulativeBlocked = SlitPlacementRecoveryPlanner.ValidateOutboundAndReturnReserve(
            limits, 70d / 3600, attempts: 1, segmentMagnitudeDegrees: 30d / 3600);

        Assert.Equal(GateDisposition.Passed, passed.Disposition);
        Assert.Equal("SLIT_SEGMENT_RETURN_ATTEMPT_RESERVE_LIMIT", attemptBlocked.Code);
        Assert.Equal("SLIT_SEGMENT_RETURN_CUMULATIVE_RESERVE_LIMIT", cumulativeBlocked.Code);
    }

    [Fact]
    public void ReturnPlannerUsesReportedPositionNotAnticipatedCommand()
    {
        var state = Pending() with
        {
            CommandedRaDegrees = 10.001,
            CommandedDeclinationDegrees = 20,
        };

        var alreadyAtOrigin = SlitPlacementRecoveryPlanner.PlanNextReturnStep(
            state,
            reportedRaDegrees: state.SegmentOriginRaDegrees,
            reportedDeclinationDegrees: state.SegmentOriginDeclinationDegrees,
            arrivalToleranceArcseconds: 2);

        Assert.True(alreadyAtOrigin.AlreadyAtOrigin);
        Assert.Equal(0, alreadyAtOrigin.CommandMagnitudeDegrees);
    }

    [Fact]
    public void AcceptedThenPartiallyCompletedCommandReturnsFromActualOffset()
    {
        var state = Pending();

        var result = SlitPlacementRecoveryPlanner.PlanNextReturnStep(
            state,
            reportedRaDegrees: state.SegmentOriginRaDegrees,
            reportedDeclinationDegrees: state.SegmentOriginDeclinationDegrees + 10d / 3600,
            arrivalToleranceArcseconds: 2);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.False(result.AlreadyAtOrigin);
        Assert.Equal(10d / 3600, result.CommandMagnitudeDegrees, 8);
        Assert.Equal(state.SegmentOriginDeclinationDegrees, result.CommandedDeclinationDegrees, 8);
    }

    [Fact]
    public async Task CrashAfterMoveIntentCanBeAdoptedFromDurableStateAtActualOrigin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-slit-crash-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "pending.json");
        try
        {
            await SlitPlacementPendingStore.WriteAtomicAsync(path, Pending());
            var recovered = await SlitPlacementPendingStore.LoadAsync(path);

            var result = SlitPlacementRecoveryPlanner.PlanNextReturnStep(
                recovered.State!,
                recovered.State!.SegmentOriginRaDegrees,
                recovered.State.SegmentOriginDeclinationDegrees,
                arrivalToleranceArcseconds: 2);

            Assert.True(result.AlreadyAtOrigin);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReturnPlannerBlocksManualPositionOutsideSegmentEnvelope()
    {
        var state = Pending();

        var result = SlitPlacementRecoveryPlanner.PlanNextReturnStep(
            state,
            reportedRaDegrees: state.SegmentOriginRaDegrees,
            reportedDeclinationDegrees: state.SegmentOriginDeclinationDegrees + 60d / 3600,
            arrivalToleranceArcseconds: 2);

        Assert.Equal(GateDisposition.Indeterminate, result.Gate.Disposition);
        Assert.Equal("SLIT_RETURN_OUTSIDE_SEGMENT_ENVELOPE", result.Gate.Code);
    }

    [Fact]
    public void ReturnPlannerHandlesRaWrapAndHighDeclinationWithoutOversizedStep()
    {
        var state = Pending() with
        {
            SegmentOriginRaDegrees = 359.9998,
            SegmentOriginDeclinationDegrees = 70,
            PriorReportedRaDegrees = 359.9998,
            PriorReportedDeclinationDegrees = 70,
            CommandedRaDegrees = 0.0002,
            CommandedDeclinationDegrees = 70 + 10d / 3600,
        };

        var result = SlitPlacementRecoveryPlanner.PlanNextReturnStep(
            state,
            reportedRaDegrees: 0.0002,
            reportedDeclinationDegrees: 70 + 10d / 3600,
            arrivalToleranceArcseconds: 2);

        Assert.Equal(GateDisposition.Passed, result.Gate.Disposition);
        Assert.InRange(result.CommandMagnitudeDegrees * 3600, 9, 11);
        Assert.Equal(state.SegmentOriginRaDegrees, result.CommandedRaDegrees, 6);
        Assert.Equal(state.SegmentOriginDeclinationDegrees, result.CommandedDeclinationDegrees, 6);
    }

    [Fact]
    public void ReturnPlannerFailsClosedAtCoordinateSingularity()
    {
        var state = Pending() with
        {
            SegmentOriginRaDegrees = 1,
            SegmentOriginDeclinationDegrees = 90,
            PriorReportedRaDegrees = 1,
            PriorReportedDeclinationDegrees = 90 - 10d / 3600,
            CommandedRaDegrees = 1,
            CommandedDeclinationDegrees = 90 - 10d / 3600,
        };

        var result = SlitPlacementRecoveryPlanner.PlanNextReturnStep(
            state,
            reportedRaDegrees: 1,
            reportedDeclinationDegrees: 90 - 10d / 3600,
            arrivalToleranceArcseconds: 2);

        Assert.Equal("SLIT_RETURN_COORDINATE_SINGULAR", result.Gate.Code);
        Assert.NotEqual(GateDisposition.Passed, result.Gate.Disposition);
    }

    [Fact]
    public async Task StoreRoundTripsAtomicallyAndRejectsCorruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "uvex-slit-pending-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "pending.json");
        try
        {
            var state = Pending();
            await SlitPlacementPendingStore.WriteAtomicAsync(path, state);

            var loaded = await SlitPlacementPendingStore.LoadAsync(path);

            Assert.Null(loaded.Error);
            Assert.Equal(state, loaded.State);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));

            var tampered = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            tampered["state"]!["segmentOriginRaDegrees"] = 11;
            await File.WriteAllTextAsync(path, tampered.ToJsonString());
            var parseableCorruption = await SlitPlacementPendingStore.LoadAsync(path);
            Assert.Null(parseableCorruption.State);
            Assert.Contains("SHA-256", parseableCorruption.Error, StringComparison.Ordinal);

            await File.WriteAllTextAsync(path, "{broken");
            var corrupt = await SlitPlacementPendingStore.LoadAsync(path);
            Assert.Null(corrupt.State);
            Assert.NotNull(corrupt.Error);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoveryFindsOutstandingStateFromAnEarlierRunDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "uvex-slit-discovery-" + Guid.NewGuid().ToString("N"));
        var oldPath = Path.Combine(root, "old-run", "control", "slit-placement-pending.json");
        var settledPath = Path.Combine(root, "settled-run", "control", "slit-placement-pending.json");
        try
        {
            await SlitPlacementPendingStore.WriteAtomicAsync(oldPath, Pending() with
            {
                ObservationRunId = "old-run",
                Phase = SlitPlacementPendingPhase.ReturnRequired,
            });
            await SlitPlacementPendingStore.WriteAtomicAsync(settledPath, Pending() with
            {
                ObservationRunId = "settled-run",
                Phase = SlitPlacementPendingPhase.SettledBudgetLedger,
            });

            var discovered = await SlitPlacementPendingStore.DiscoverAsync(root);

            Assert.Equal(2, discovered.Count);
            var outstanding = Assert.Single(
                discovered,
                item => item.State?.Phase != SlitPlacementPendingPhase.SettledBudgetLedger);
            Assert.Equal(Path.GetFullPath(oldPath), outstanding.Path);
            Assert.Equal("old-run", outstanding.State!.ObservationRunId);
            Assert.All(discovered, item => Assert.Null(item.Error));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SlitPlacementPendingState Pending() => new(
        SlitPlacementPendingState.CurrentSchemaVersion,
        "run-1",
        "0123456789abcdef0123456789abcdef",
        new string('a', 64),
        new string('c', 64),
        new string('b', 64),
        "transform-1",
        "pierEast",
        "J2000",
        SegmentOriginRaDegrees: 10,
        SegmentOriginDeclinationDegrees: 20,
        PriorReportedRaDegrees: 10,
        PriorReportedDeclinationDegrees: 20,
        CommandedRaDegrees: 10,
        CommandedDeclinationDegrees: 20 + 30d / 3600,
        CommandMagnitudeDegrees: 30d / 3600,
        PreMoveResidualPixels: 100,
        MaximumSingleCorrectionDegrees: 30d / 3600,
        MaximumCumulativeCorrectionDegrees: 120d / 3600,
        MaximumCorrectionAttempts: 4,
        MaximumAcquisitionSeconds: 720,
        CumulativeCorrectionDegrees: 30d / 3600,
        CorrectionAttempts: 1,
        FineAcquisitionStartedUtc: DateTimeOffset.Parse("2026-08-18T11:55:00Z"),
        CreatedUtc: DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
        UpdatedUtc: DateTimeOffset.Parse("2026-08-18T12:00:00Z"),
        Phase: SlitPlacementPendingPhase.MoveIntent);
}
