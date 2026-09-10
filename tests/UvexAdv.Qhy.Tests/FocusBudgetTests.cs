using UvexAdv.Qhy.Core;

namespace UvexAdv.Qhy.Tests;

public sealed class FocusBudgetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "uvex-focus-ledger-tests", Guid.NewGuid().ToString("N"));
    private static QhyFocusRunPolicy Policy => new("fixture-focuser", 1000, 800, 1200, 80, 160, 10, 1);

    [Fact]
    public void NativeOvershootReservesBothLegsAndConfirmsOnlyTheActualEndpoint()
    {
        var journal = new List<QhyFocusBudgetState>();
        var budget = new QhyFocusBudget(Policy, journal.Add);
        Assert.Equal(950, budget.Reserve(1000, -50, -10));
        Assert.Equal(70, budget.CumulativeSteps);
        Assert.True(journal[0].Pending);
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(950, 10));
        Assert.Throws<InvalidOperationException>(() => budget.Confirm(951));
        Assert.True(budget.Pending);
        budget.Confirm(950);
        Assert.False(journal[^1].Pending);
        Assert.Equal(970, budget.Reserve(950, 20));
        Assert.Equal(90, budget.CumulativeSteps);
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(-50, -11)]
    [InlineData(81, 0)]
    [InlineData(10, 10)]
    public void WrongDirectionBacklashAndSingleBudgetCannotReachTheOwner(int delta, int overshoot)
    {
        var written = false;
        var budget = new QhyFocusBudget(Policy, _ => written = true);
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(1000, delta, overshoot));
        Assert.False(written);
        Assert.Equal(0, budget.CumulativeSteps);
    }

    [Fact]
    public void FailedDurableWriteDoesNotAuthorizeMovementOrConfirmAnEndpoint()
    {
        var fail = true;
        var budget = new QhyFocusBudget(Policy, _ => { if (fail) throw new IOException("fixture-disk-full"); });
        Assert.Throws<IOException>(() => budget.Reserve(1000, 20));
        Assert.Equal(1000, budget.ExpectedPosition);
        fail = false; budget.Reserve(1000, 20); fail = true;
        Assert.Throws<IOException>(() => budget.Confirm(1020));
        Assert.True(budget.Pending);
    }

    [Fact]
    public void RestartRetainsPendingObligationAndCannotIssueANewRunBudget()
    {
        var budget = QhyFocusBudgetStore.Open(root, "run", "fixture-config", Policy);
        budget.Reserve(1000, 30);
        var restored = QhyFocusBudgetStore.Open(root, "run", "fixture-config", Policy);
        Assert.True(restored.Pending);
        Assert.Equal(30, restored.CumulativeSteps);
        Assert.Throws<InvalidOperationException>(() => restored.Reserve(1030, 10));
        Assert.Throws<InvalidOperationException>(() => QhyFocusBudgetStore.Open(root, "new-run", "fixture-config", Policy));
        restored.Confirm(1030);
        Assert.Equal(30, QhyFocusBudgetStore.Open(root, "run", "fixture-config", Policy).CumulativeSteps);
        Assert.Throws<InvalidOperationException>(() => QhyFocusBudgetStore.Open(root, "run", "changed-config", Policy));
        Assert.Throws<InvalidOperationException>(() => QhyFocusBudgetStore.Open(root, "run", "fixture-config", Policy with { MaximumCumulativeMoveSteps = 300 }));
    }

    [Fact]
    public void BothIntermediatePositionAndCumulativeBudgetAreEnforced()
    {
        var budget = new QhyFocusBudget(Policy with { StartPositionSteps = 820 }, _ => { });
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(820, -15, -10));
        var repeated = new QhyFocusBudget(Policy, _ => { });
        repeated.Reserve(1000, 70); repeated.Confirm(1070);
        repeated.Reserve(1070, -50, -10); repeated.Confirm(1020);
        Assert.Throws<InvalidOperationException>(() => repeated.Reserve(1020, 21));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
