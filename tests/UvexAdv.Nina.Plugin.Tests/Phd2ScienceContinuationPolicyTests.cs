using UvexAdv.Nina.Plugin;
using UvexAdv.Phd2;
using UvexAdv.Observatory;
using Xunit;

namespace UvexAdv.Nina.Plugin.Tests;

public sealed class Phd2ScienceContinuationPolicyTests
{
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
