using UvexAdv.Nina.Plugin;
using UvexAdv.Phd2;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2ScienceContinuationPolicyTests
{
    [Theory]
    [InlineData("same", true)]
    [InlineData("external-lock", false)]
    [InlineData("reconnect", false)]
    [InlineData("calibration", false)]
    [InlineData("lock", false)]
    [InlineData("selected", false)]
    [InlineData("missing-event", false)]
    [InlineData("gap", false)]
    [InlineData("prolonged-loss", false)]
    [InlineData("no-output", false)]
    [InlineData("replayed-frame", false)]
    [InlineData("pending-settle", false)]
    public void Hd223554RecoveredGuideRoutesToLocalRecoveryOnlyWithNativeLossChain(string variant, bool expected)
    {
        var selected = new Phd2Point(1402.33, 374.88);
        var endpoint = new Phd2Point(1410.94, 398.35);
        var loss = new Phd2GuideEpochChange(17, 18, "event:StarLost", 201, Start,
            Phd2AppState.Guiding, Phd2AppState.LostLock);
        var found = new Phd2GuideEpochChange(18, 19, "event:GuideStep", 203, Start.AddSeconds(2.4),
            Phd2AppState.LostLock, Phd2AppState.Guiding);
        var step = new Phd2GuideStep(59, 0, 0, 19.1, 1, 37, 72);
        var state = State() with { GuideEpoch = 19, SelectedStar = selected, LockPosition = endpoint,
            LastGuideStep = step, GuideOutput = new(59, 0, Start, Start.AddSeconds(3), false, step),
            RecentGuideEpochChanges = [loss, found] };
        state = variant switch
        {
            "external-lock" => state with { RecentGuideEpochChanges = [loss, found with { Cause = "event:LockPositionSet" }] },
            "reconnect" => state with { ConnectionEpoch = 2 },
            "calibration" => state with { CalibrationChangeSequence = 1 },
            "lock" => state with { LockPosition = selected },
            "selected" => state with { SelectedStar = endpoint },
            "missing-event" => state with { RecentGuideEpochChanges = [found] },
            "gap" => state with { RecentGuideEpochChanges = [loss, found with { PreviousEpoch = 16 }] },
            "prolonged-loss" => state with { RecentGuideEpochChanges = [loss, found with { ObservedUtc = Start.AddMinutes(3) }] },
            "no-output" => state with { GuideOutput = null },
            "replayed-frame" => state with { LastGuideStep = step with { Frame = 58 } },
            "pending-settle" => state with { PendingSettleOperationId = 1 },
            _ => state,
        };
        Assert.Equal(expected, Phd2ScienceContinuationPolicy.IsNativeLossRecovery(state, 1, 17, 0, selected, endpoint, .05));
        var ledger = Ledger() with { GuideEpoch = 17, OriginLockX = selected.X, OriginLockY = selected.Y,
            CurrentLockX = endpoint.X, RequestedLockX = endpoint.X, CurrentLockY = endpoint.Y,
            RequestedLockY = endpoint.Y, CumulativeCommandedPixels = 25, AttemptsUsed = 1,
            ExcludedAtrExposureSeconds = 29.4, UpdatedUtc = Start.AddSeconds(131) };
        var proof = new Phd2PostLockGuidingObservation("native-loss-window-replay", 1, 19, 230, endpoint, .05,
            Start.AddSeconds(132), YieldedToFreshResiduals: true);
        var frames = Enumerable.Range(1, 3).Select(i => new Phd2GuidingFrameResult(
            Path.GetFullPath($"new-{i}.fit"), new string((char)('a' + i), 64), 59+i, 230+i,
            Start.AddSeconds(132+i), Start.AddSeconds(133+i), false, false, false, false)).ToArray();
        var now = Start.AddSeconds(137);
        Assert.Null(Phd2ScienceContinuationPolicy.BindVerifiedLossWindow(ledger, proof, state, selected, endpoint, 0, frames[^1], now));
        if (expected)
        {
            Assert.Throws<Phd2Exception>(() => proof.AcceptResiduals([frames[0], frames[0], frames[2]], state));
            var fresh = proof.AcceptResiduals(frames, state);
            var rebound = Phd2ScienceContinuationPolicy.BindVerifiedLossWindow(ledger, fresh, state, selected, endpoint, 0, frames[^1], now);
            Assert.NotNull(rebound);
            Assert.Equal(ledger, rebound with { GuideEpoch = ledger.GuideEpoch, LastFramePath = ledger.LastFramePath,
                LastAcceptedFrameSha256 = ledger.LastAcceptedFrameSha256, LastReason = ledger.LastReason, UpdatedUtc = ledger.UpdatedUtc });
            Assert.True(Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(rebound, state, 1, 19,
                fresh.HasAcceptedWindow(state), endpoint, .05));
            Assert.Null(Phd2ScienceContinuationPolicy.BindVerifiedLossWindow(ledger with { Phase = Phd2LockShiftPendingPhase.ReturnRequired },
                fresh, state, selected, endpoint, 0, frames[^1], now));
        }
        Assert.True(AdaptiveGuidingRecovery.Handles(ObservationStage.SelectAtrExposure, "GUIDING_LOST"));
        Assert.False(AdaptiveGuidingRecovery.Handles(ObservationStage.SelectAtrExposure, "PHD2_SCIENCE_GUIDE_EPOCH_CHANGED"));
        var budget = new AdaptiveGuidingRecovery();
        Assert.True(budget.TryConsumeRecoveredEpochWindow());
        Assert.False(budget.TryConsumeRecoveredEpochWindow());
    }
    [Theory]
    [InlineData("GUIDING_LOST", true)]
    [InlineData("GUIDING_UNSTABLE", true)]
    [InlineData("GUIDING_NOT_STABLE", true)]
    [InlineData("PHD2_SCIENCE_GUIDE_EPOCH_CHANGED", false)]
    [InlineData("G3_POST_WCS_SHORT_UNCONFIRMED", false)]
    public void OnlySameEpochReadOnlyCheckMayPrecedeMotionLedgerFunding(
        string code, bool expected) =>
        Assert.Equal(expected, Phd2ScienceContinuationPolicy.MayVerifySettledGuideInPlace(code));

    [Theory]
    [InlineData("GUIDING_LOST")]
    [InlineData("GUIDING_UNSTABLE")]
    [InlineData("GUIDING_NOT_STABLE")]
    public void StaleSettleAfterCompletedProbeCanResumeOnlyTheSameSelectedStarAndLock(string sourceCode)
    {
        var ledger = Ledger() with
        {
            OriginLockX = 10,
            OriginLockY = 20,
            CurrentLockX = 30,
            CurrentLockY = 40,
            RequestedLockX = 30,
            RequestedLockY = 40,
        };
        var state = State() with
        {
            LockPosition = new(30, 40),
            SelectedStar = new(10, 20),
            GuideOutput = new(
                1, 0, Start, Start, false,
                new Phd2GuideStep(1, 0, 0, 100, 3, 0, 0)),
        };

        Assert.True(Phd2ScienceContinuationPolicy.CanResettleAfterExposureAtSettledLock(
            sourceCode, ledger, state, 1, 86, new(10, 20), new(30, 40), new(30, 40), 0.05));
    }

    [Theory]
    [InlineData(86)]
    [InlineData(87)]
    public void NativeGuideSelfRecoveryAcceptsOnlyTheSameLiveFieldAndSelectedLock(long guideEpoch)
    {
        var state = State() with
        {
            GuideEpoch = guideEpoch,
            LockPosition = new(30, 40),
            SelectedStar = new(10, 20),
            GuideOutput = new(
                1, 0, Start, Start, false,
                new Phd2GuideStep(1, 0, 0, 100, 3, 0, 0)),
        };

        Assert.True(Phd2ScienceContinuationPolicy.IsSameFieldGuideActiveAfterLoss(
            state, 1, new(10, 20), new(30, 40), 0.05));
    }

    [Theory]
    [InlineData("LOST_LOCK")]
    [InlineData("SELECTION_CHANGED")]
    [InlineData("LOCK_CHANGED")]
    [InlineData("CONNECTION_CHANGED")]
    [InlineData("OUTPUT_FAILED")]
    [InlineData("PENDING_SETTLE")]
    [InlineData("PAUSED")]
    public void NativeGuideSelfRecoveryRejectsChangedOrUnsafeState(string kind)
    {
        var state = State() with
        {
            LockPosition = new(30, 40),
            SelectedStar = new(10, 20),
            GuideOutput = new(
                1, 0, Start, Start, kind == "OUTPUT_FAILED",
                new Phd2GuideStep(1, 0, 0, 100, 3, 0, 0)),
        };
        state = kind switch
        {
            "LOST_LOCK" => state with { AppState = Phd2AppState.LostLock },
            "SELECTION_CHANGED" => state with { SelectedStar = new(11, 20) },
            "LOCK_CHANGED" => state with { LockPosition = new(31, 40) },
            "CONNECTION_CHANGED" => state with { ConnectionEpoch = 2 },
            "PENDING_SETTLE" => state with { PendingSettleOperationId = 9 },
            "PAUSED" => state with { Phd2Paused = true },
            _ => state,
        };

        Assert.False(Phd2ScienceContinuationPolicy.IsSameFieldGuideActiveAfterLoss(
            state, 1, new(10, 20), new(30, 40), 0.05));
    }

    [Theory]
    [InlineData("LOST_STATE")]
    [InlineData("GUIDE_EPOCH_CHANGED")]
    [InlineData("SELECTION_CHANGED")]
    [InlineData("LOCK_CHANGED")]
    [InlineData("OUTPUT_FAILED")]
    [InlineData("PENDING_SETTLE")]
    [InlineData("PAUSED")]
    public void StaleSettleRecoveryRejectsEveryChangedOrUnsafeOwnerState(string kind)
    {
        var ledger = Ledger() with
        {
            OriginLockX = 10,
            OriginLockY = 20,
            CurrentLockX = 30,
            CurrentLockY = 40,
            RequestedLockX = 30,
            RequestedLockY = 40,
        };
        var state = State() with
        {
            LockPosition = new(30, 40),
            SelectedStar = new(10, 20),
            GuideOutput = new(
                1, 0, Start, Start, kind == "OUTPUT_FAILED",
                new Phd2GuideStep(1, 0, 0, 100, 3, 0, 0)),
        };
        state = kind switch
        {
            "LOST_STATE" => state with { AppState = Phd2AppState.LostLock },
            "GUIDE_EPOCH_CHANGED" => state with { GuideEpoch = 87 },
            "SELECTION_CHANGED" => state with { SelectedStar = new(11, 20) },
            "LOCK_CHANGED" => state with { LockPosition = new(31, 40) },
            "PENDING_SETTLE" => state with { PendingSettleOperationId = 9 },
            "PAUSED" => state with { AutomationPaused = true },
            _ => state,
        };
        var actualLock = kind == "LOCK_CHANGED" ? new Phd2Point(31, 40) : new Phd2Point(30, 40);
        var sourceCode = kind == "LOST_STATE" ? "GUIDING_LOST" : "GUIDING_UNSTABLE";

        Assert.False(Phd2ScienceContinuationPolicy.CanResettleAfterExposureAtSettledLock(
            sourceCode, ledger, state, 1, 86, new(10, 20), new(30, 40), actualLock, 0.05));
    }

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-11T16:17:15Z");
    private static Phd2LockShiftPendingState Ledger() => new(
        3, "run", new string('a', 32), new string('A', 64), new string('B', 64), new string('C', 64),
        "policy", new string('D', 64), new string('E', 64), 0, 1, 86,
        1782.82, 427.27, 1785.13, 447.2, 1785.13, 447.2,
        25, 100, 8, 300, 20.4698, 3, Start, Start, Start.AddSeconds(27),
        Phd2LockShiftPendingPhase.SettledBudgetLedger, new string('F', 64), "frame.fit", null, "warning-probe",
        816, 413, 817, 428);
    private static Phd2StateSnapshot State() => Phd2StateSnapshot.Disconnected with
    { IsConnected = true, AppState = Phd2AppState.Guiding, ConnectionEpoch = 1, GuideEpoch = 86 };

    [Theory]
    [InlineData("time", "skip")] [InlineData("time", "cleanup-failure")] [InlineData("time", "end-policy")]
    [InlineData("attempt", "skip")] [InlineData("attempt", "cleanup-failure")] [InlineData("attempt", "end-policy")]
    [InlineData("pixels", "skip")] [InlineData("pixels", "cleanup-failure")] [InlineData("pixels", "end-policy")]
    [InlineData("binding", "unknown-gate")] [InlineData("invalid", "unknown-gate")]
    [InlineData("pending", "unknown-gate")] [InlineData("clock", "unknown-gate")]
    public async Task ScienceRebuildBudgetIsNotConfusedWithInvalidLedgerAndActuallyContinuesNativeNight(string failure, string boundary)
    {
        var ledger = failure switch
        {
            "attempt" => Ledger() with { AttemptsUsed = 8 },
            "pixels" => Ledger() with { CumulativeCommandedPixels = 100 },
            "invalid" => Ledger() with { CumulativeCommandedPixels = double.NaN },
            "pending" => Ledger() with { Phase = Phd2LockShiftPendingPhase.ReturnRequired },
            _ => Ledger(),
        };
        var binding = failure == "binding" ? GateResult.Unknown("PHD2_LOCK_LEDGER_BINDING_CHANGED", "binding") : GateResult.Pass("bound", "fixture");
        var gate = Phd2ScienceContinuationPolicy.RebuildBudgetGate(ledger,
            Start.AddSeconds(failure == "time" ? 600 : failure == "clock" ? -1 : 50),
            GateResult.Unknown("GUIDING_LOST", "original star loss"), binding)!;
        Assert.NotNull(gate);
        if (boundary != "unknown-gate")
        {
            Assert.Equal("PHD2_SCIENCE_RECOVERY_BUDGET_UNAVAILABLE", gate.Code);
            Assert.Contains("GUIDING_LOST", gate.Message);
            Assert.Equal(ledger.AttemptsUsed, gate.Metrics!["attemptsUsed"]);
        }
        await new NativeSequencePlanTests().AssertNativeFailureBoundaryAsync(boundary, gate.Code, gate, ObservationStage.RunScienceBlock);
    }

    [Fact]
    public void PreExposureResumePreservesOriginChargesConsumedFrameAndExposureClock()
    {
        var state = Credit(Ledger(), 100, 700, 600, 600)!;
        var resumed = Phd2ScienceContinuationPolicy.ResumeMotionLedger(state, Start.AddSeconds(700));
        Assert.Equal(state.ToPlannerLedger(), resumed);
        Assert.Equal(new Phd2Point(state.OriginLockX, state.OriginLockY), resumed!.OriginLockPosition);
        Assert.NotEqual(resumed.OriginLockPosition, resumed.CurrentLockPosition);
        Assert.Equal(state.LastAcceptedFrameSha256, resumed.LastAcceptedFrameSha256);
        Assert.Null(Phd2ScienceContinuationPolicy.ResumeMotionLedger(state, Start.AddSeconds(900)));
        Assert.Null(Phd2ScienceContinuationPolicy.ResumeMotionLedger(state with { AttemptsUsed = 8 }, Start.AddSeconds(700)));
        Assert.Null(Phd2ScienceContinuationPolicy.ResumeMotionLedger(state with { Phase = Phd2LockShiftPendingPhase.ReturnRequired }, Start.AddSeconds(700)));
    }

    [Fact]
    public void Real601SecondExampleAllowsOnlyExistingLockObservationWithoutResettingBudget()
    {
        var ledger = Ledger();
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(ledger, Start.AddSeconds(601.4862)));
        Assert.True(Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(ledger, State(), 1, 86, true, new(1785.13, 447.2), 0.05));
        Assert.Equal(Start, ledger.StartedUtc);
        Assert.Equal(3, ledger.AttemptsUsed);
        Assert.Equal(20.4698, ledger.CumulativeCommandedPixels);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("connection")]
    [InlineData("lost")]
    [InlineData("paused")]
    [InlineData("intent")]
    [InlineData("no-proof")]
    [InlineData("changed-lock")]
    [InlineData("pending-settle")]
    [InlineData("nan")]
    public void ObservationShortcutNeverReusesChangedOrUnprovenPhysicalState(string kind)
    {
        var ledger = Ledger(); var state = State(); var point = new Phd2Point(1785.13, 447.2);
        state = kind switch
        {
            "epoch" => state with { GuideEpoch = 89 },
            "connection" => state with { ConnectionEpoch = 2 },
            "lost" => state with { AppState = Phd2AppState.LostLock },
            "paused" => state with { AutomationPaused = true },
            "pending-settle" => state with { PendingSettleOperationId = 10 },
            _ => state,
        };
        if (kind == "intent") ledger = ledger with { Phase = Phd2LockShiftPendingPhase.StageIntent };
        if (kind == "changed-lock") point = point with { X = 1700 };
        if (kind == "nan") point = point with { X = double.NaN };
        Assert.False(Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(ledger, state, 1, 86, kind != "no-proof", point, 0.05));
    }

    [Fact]
    public void ReplacementStillConsumesOriginalAttemptsPixelsAndTime()
    {
        Assert.True(Phd2ScienceContinuationPolicy.HasReplacementBudget(Ledger(), Start.AddSeconds(50)));
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(Ledger() with { AttemptsUsed = 8 }, Start.AddSeconds(50)));
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(Ledger() with { CumulativeCommandedPixels = 100 }, Start.AddSeconds(50)));
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(Ledger(), Start.AddSeconds(300)));
    }

    [Fact]
    public void Completed600SecondExposurePreservesOriginalActiveTimeAndAllMotionLimits()
    {
        var before = Ledger();
        var after = Credit(before, 200, 805, 600, 605)!;
        Assert.NotNull(after);
        Assert.Equal(600, after.ExcludedAtrExposureSeconds);
        Assert.Equal(Start, after.StartedUtc);
        Assert.Equal(before.AttemptsUsed, after.AttemptsUsed);
        Assert.Equal(before.CumulativeCommandedPixels, after.CumulativeCommandedPixels);
        Assert.Equal(before.MaximumElapsedSeconds, after.MaximumElapsedSeconds);
        Assert.Equal(Start.AddSeconds(600), after.ToPlannerLedger().StartedUtc);
        Assert.True(Phd2ScienceContinuationPolicy.HasReplacementBudget(after, Start.AddSeconds(805)));
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(after, Start.AddSeconds(900)));
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(after with { AttemptsUsed = 8 }, Start.AddSeconds(805)));
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(after with { CumulativeCommandedPixels = 100 }, Start.AddSeconds(805)));
        Assert.Equal(Start.AddSeconds(1000), after.ToRecoveryEpisodePlannerLedger(Start.AddSeconds(1000)).StartedUtc);
    }

    [Fact]
    public void SameFieldGuideEpochRebasePreservesBudgetAndTranslatesOnlyTheCompletedEndpoint()
    {
        var before = Ledger() with
        {
            OriginLockX = 10,
            OriginLockY = 20,
            CurrentLockX = 30,
            CurrentLockY = 40,
            RequestedLockX = 30,
            RequestedLockY = 40,
            ExcludedAtrExposureSeconds = 600,
            UpdatedUtc = Start.AddSeconds(700),
        };
        var now = Start.AddSeconds(700);
        var rebased = before.RebaseAfterSameFieldGuideEpoch(
            1, 87, new(100, 200), .05, now,
            Path.Combine(Path.GetTempPath(), "same-field-rebased.fit"),
            new string('D', 64),
            "recorded same-field thin-cloud recovery");

        Assert.Equal(80, rebased.OriginLockX);
        Assert.Equal(180, rebased.OriginLockY);
        Assert.Equal(100, rebased.CurrentLockX);
        Assert.Equal(200, rebased.CurrentLockY);
        Assert.Equal(100, rebased.RequestedLockX);
        Assert.Equal(200, rebased.RequestedLockY);
        Assert.Equal(87, rebased.GuideEpoch);
        Assert.Equal(before.AttemptsUsed, rebased.AttemptsUsed);
        Assert.Equal(before.CumulativeCommandedPixels, rebased.CumulativeCommandedPixels);
        Assert.Equal(before.StartedUtc, rebased.StartedUtc);
        Assert.Equal(before.ExcludedAtrExposureSeconds, rebased.ExcludedAtrExposureSeconds);
        Assert.Equal(Phd2LockShiftPendingPhase.SettledBudgetLedger, rebased.Phase);
        Assert.Empty(rebased.Validate());
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("past-epoch")]
    [InlineData("ambiguous-endpoint")]
    [InlineData("pending")]
    public void SameFieldGuideEpochRebaseRejectsChangedOrUnprovenOwnership(string failure)
    {
        var state = Ledger() with
        {
            CurrentLockX = failure == "ambiguous-endpoint" ? 31 : 30,
            CurrentLockY = 40,
            RequestedLockX = 30,
            RequestedLockY = 40,
        };
        if (failure == "pending")
            state = state with { Phase = Phd2LockShiftPendingPhase.ReturnRequired };
        Assert.ThrowsAny<Exception>(() => state.RebaseAfterSameFieldGuideEpoch(
            failure == "connection" ? 2 : 1,
            failure == "past-epoch" ? 85 : 87,
            new(100, 200), .05, Start.AddSeconds(100),
            Path.Combine(Path.GetTempPath(), "same-field-rejected.fit"),
            new string('E', 64),
            "negative fixture"));
    }

    [Fact]
    public void ReportedEightProbeDurationsDoNotBecomeFineMotionTime()
    {
        var ledger = Ledger();
        var cursor = 200d;
        foreach (var seconds in new[] { 0.1, 0.3, 1, 3, 10, 15, 30, 600 })
        {
            ledger = Credit(ledger, cursor, cursor + seconds, seconds, seconds)!;
            cursor += seconds;
        }
        Assert.Equal(659.4, ledger.ExcludedAtrExposureSeconds, 6);
        Assert.Equal(208.8259, (Start.AddSeconds(868.2259) - ledger.MotionClockStartedUtc).TotalSeconds, 5);
        Assert.True(Phd2ScienceContinuationPolicy.HasReplacementBudget(ledger, Start.AddSeconds(868.2259)));
        // Credit is only accounting: a lost guide epoch still rejects the observation shortcut.
        Assert.False(Phd2ScienceContinuationPolicy.CanObserveAtSettledLock(ledger,
            State() with { AppState = Phd2AppState.LostLock, GuideEpoch = 142 },
            1, 86, true, new(1785.13, 447.2), 0.05));
    }

    [Theory]
    [InlineData(620, 600, 620, 600)]
    [InlineData(600, 600, 590, 590)]
    [InlineData(580, 600, 600, 580)]
    public void CreditNeverExceedsRequestedExposureWallOrMonotonicTime(double wall, double requested, double monotonic, double expected)
    {
        Assert.Equal(expected, Credit(Ledger(), 50, 50 + wall, requested, monotonic)!.ExcludedAtrExposureSeconds);
    }

    [Fact]
    public void ExposureDoesNotReviveAlreadyExpiredMotionTimeOrExcludePause()
    {
        var expired = Credit(Ledger(), 350, 950, 600, 600)!;
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(expired, Start.AddSeconds(950)));
        var completed = Credit(Ledger(), 100, 700, 600, 600)!;
        Assert.False(Phd2ScienceContinuationPolicy.HasReplacementBudget(completed, Start.AddSeconds(1000)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void NoCreditForOutstandingOrRetiredMotion(int phase)
    {
        Assert.Null(Credit(Ledger() with { Phase = (Phd2LockShiftPendingPhase)phase }, 50, 650, 600, 600));
    }

    [Fact]
    public void NoCreditForChangedLedgerDuplicateOrOverlappingCapture()
    {
        var before = Ledger();
        var after = Credit(before, 50, 650, 600, 600)!;
        Assert.Null(Phd2ScienceContinuationPolicy.CreditExposure(before, after, Start.AddSeconds(50), Start.AddSeconds(650), 600, 600));
        Assert.Null(Credit(after, 50, 650, 600, 600));
        Assert.Null(Credit(before, 20, 620, 600, 600));
        Assert.Null(Phd2ScienceContinuationPolicy.CreditExposure(before, before with { AttemptsUsed = 4 }, Start.AddSeconds(50), Start.AddSeconds(650), 600, 600));
        Assert.Null(Credit(before, 650, 50, 600, 600));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidDurationsAreNeverCredited(double invalid)
    {
        Assert.Null(Credit(Ledger(), 50, 650, invalid, 600));
        Assert.Null(Credit(Ledger(), 50, 650, 600, invalid));
    }

    [Fact]
    public async Task DurableStoreReadsHistoricalZeroFieldAndPreservesNonzeroCreditAndHash()
    {
        var path = Path.Combine(Path.GetTempPath(), "uvex-clock-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var before = Ledger();
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, before, CancellationToken.None);
            var historical = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("excludedAtrExposureSeconds", historical);
            Assert.DoesNotContain("motionClockStartedUtc", historical);
            Assert.Equal(before, (await Phd2LockShiftPendingStore.LoadAsync(path, CancellationToken.None)).State);
            var after = Credit(before, 50, 650, 600, 600)!;
            await Phd2LockShiftPendingStore.WriteAtomicAsync(path, after, CancellationToken.None);
            Assert.Equal(after, (await Phd2LockShiftPendingStore.LoadAsync(path, CancellationToken.None)).State);
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"excludedAtrExposureSeconds\": 600", json);
            await File.WriteAllTextAsync(path, json.Replace("\"excludedAtrExposureSeconds\": 600", "\"excludedAtrExposureSeconds\": 601"));
            Assert.Contains("SHA-256", (await Phd2LockShiftPendingStore.LoadAsync(path, CancellationToken.None)).Error!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HandoffCannotDropOrInflateCreditedExposure()
    {
        var source = Credit(Ledger(), 50, 650, 600, 600)!;
        source = source with { CurrentLockX = source.OriginLockX, CurrentLockY = source.OriginLockY,
            RequestedLockX = source.OriginLockX, RequestedLockY = source.OriginLockY };
        var copy = Phd2LockShiftBudgetHandoff.CreateCurrentRunSettledCopy(source, "next", source.RecoveryContextSha256, Start.AddSeconds(651));
        Assert.Empty(Phd2LockShiftBudgetHandoff.ValidateCompletedHandoff(source, copy, "next", source.RecoveryContextSha256));
        foreach (var excluded in new[] { 0d, 601d })
            Assert.NotEmpty(Phd2LockShiftBudgetHandoff.ValidateCompletedHandoff(source, copy with { ExcludedAtrExposureSeconds = excluded }, "next", source.RecoveryContextSha256));
        Assert.NotEmpty((source with { ExcludedAtrExposureSeconds = 651 }).Validate());
        Assert.NotEmpty((source with { ExcludedAtrExposureSeconds = double.NaN }).Validate());
    }

    private static Phd2LockShiftPendingState? Credit(Phd2LockShiftPendingState before, double opened, double completed, double requested, double monotonic) =>
        Phd2ScienceContinuationPolicy.CreditExposure(before, before, Start.AddSeconds(opened), Start.AddSeconds(completed), requested, monotonic);
}
