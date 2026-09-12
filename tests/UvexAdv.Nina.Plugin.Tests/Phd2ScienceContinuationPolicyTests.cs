using UvexAdv.Nina.Plugin;
using UvexAdv.Phd2;
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
}
